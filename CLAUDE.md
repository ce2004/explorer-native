# Explorer Native

A keyboard- and screen-reader-first file manager for Windows on ARM64. WinForms,
.NET 8, no third-party packages. It exists to be *fast* and to *read well under
NVDA* — those two goals decide most of the design arguments in this codebase.

## Build and run

The SDK is not on `PATH` by default. It lives in a per-user install:

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
```

If it is missing entirely (`dotnet --list-sdks` shows nothing), reinstall with:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
.\dotnet-install.ps1 -Channel 8.0 -Architecture arm64 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
```

```powershell
dotnet build ExplorerNative.csproj          # app
dotnet build tests\SelfTest.csproj          # tests
dotnet run --project tests\SelfTest.csproj  # run the suite (~1 min)
```

The suite prints `N passed, M failed` and exits non-zero on failure. It is a
plain console program, not a test framework — `Tests.Main` calls each section in
order and `Check`/`Equal` record results.

### A trap when rebuilding

If the app is running, it holds `bin\Release\...\ExplorerNative.exe` open and the
build fails with MSB3027. Either stop it first, or publish somewhere else:

```powershell
dotnet publish ExplorerNative.csproj -c Release -o bin\app `
  -p:BaseOutputPath=obj\pubout\ -p:OutputPath=obj\pubout\
```

## Updating the installed copy

**A change that is only built is not shipped.** The registry names one absolute
path — the installed copy in `%LOCALAPPDATA%\Programs\ExplorerNative` — and that
is what every folder on the machine opens. `dotnet build` and a green suite say
the change is *correct*; they say nothing about whether anyone is running it.
Fixing a fistful of bugs, watching the tests pass and reporting it done, while
the application the user actually opens folders with is a binary from before any
of it, is a way to deliver nothing at all. It has happened. Finish the job.

**Installing does not require the application to be closed, and nothing here
waits for it to.** That is the change; the whole of the old procedure below it
followed from the opposite assumption.

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"

# 1. Green first. Never install something the suite has not passed.
dotnet build ExplorerNative.csproj
dotnet run --project tests\SelfTest.csproj

# 2. Stage a real Release single-file build. Not bin\Debug — the installed
#    copy is self-contained and single-file, and that is what has to be tested.
dotnet publish ExplorerNative.csproj -c Release -o bin\app `
  -p:BaseOutputPath=obj\pubout\ -p:OutputPath=obj\pubout\

# 3. Install by running the STAGED exe. -NoNewWindow so it inherits this
#    console and reports into it; the exit code is the answer.
$p = Start-Process bin\app\ExplorerNative.exe -ArgumentList '--install-only' `
       -PassThru -NoNewWindow
$null = $p.Handle                   # see below — without this ExitCode is empty
$p.WaitForExit(120000)
"exit code $($p.ExitCode)"          # 0 installed, 1 did not

# 4. And what it did, which it has already written down.
& bin\app\ExplorerNative.exe --read-log | Select-Object -Last 3   # logs are DPAPI-encrypted
```

Two PowerShell traps in those four lines, both of which look like the install
failing:

- **`$null = $p.Handle` is not a superstition.** `Start-Process -PassThru`
  without `-Wait` hands back a `Process` object that has let go of the OS handle,
  so `ExitCode` reads as empty however long you wait — which is indistinguishable
  from a run that told you nothing. Touching `Handle` once, before the process
  ends, caches it.
- **Never `-Wait`.** It hangs, and not because the install hangs: `--install-only`
  starts the copy it just installed, and that copy is a file manager which stays
  running. `-Wait` waits for it. `WaitForExit` on the installer's own object does
  not.

There is no step asking you to prove it by hand any more. The installer hashes
what it wrote, compares it against what it copied, and fails if they differ — the
old step 5 was a person remembering to do what the tool could do for itself.

**Run the staged executable, never the installed one.** `AppInstall.Install`
copies from the *running* process's own directory, and returns success without
doing anything when it is already the installed copy. Invoking
`%LOCALAPPDATA%\Programs\ExplorerNative\ExplorerNative.exe --install` therefore
reports that it worked and changes nothing.

**`--install-only`, not `--install`, unless the takeover is the point.**
`--install` also claims the folder-open verb for every `Directory` and `Drive` on
the machine and writes `SetAsDefaultFileExplorer` true. That is a desktop-wide
change, and it is not what "update the copy I am running" means. `--install-only`
copies the files, records `RegisteredExePath` and restarts the app, and touches
no registration at all — the next launch's `Reconcile` keeps whatever was already
set.

Rolling back: the previous binary is still in the install directory under
`ExplorerNative.exe.retired-<stamp>`, until the next sweep takes it. Copy it back
over `ExplorerNative.exe`. There is no separate backup step and no folder of
160MB copies to sweep up afterwards.

The same goes for build output. `-p:BaseOutputPath=obj\<name>\` is how a build
avoids the running app's locked binary, and every distinct `<name>` is another
168MB — `obj\agentapp`, `obj\agentaudio`, `obj\agentdrive`, `obj\agentfiles`,
`obj\landing` were all still there from earlier sessions, a gigabyte between
them. They regenerate on demand; delete them when the work is finished.

### Why it no longer waits, and what it cost to learn

**A file copy must not depend on inter-process co-operation.** The install used
to send `<exit>` down the single-instance pipe, wait up to thirty seconds for the
running copy to release its own executable, and then overwrite it — because
Windows will not let a running image be overwritten. Every part of that is
correct and the whole is fragile, because it made the simplest operation in the
program conditional on the most complicated one still working.

It stopped working. Four instances were running with **no pipe server among
them**, so:

1. `AskRunningInstanceToExit` got no answer and returned true, which it defines
   as "nothing to wait for" — indistinguishable from "nothing is running".
2. The copy then failed against a file all four were holding.
3. The failure went into a `MessageBox`, on a machine where nobody was looking
   at the screen.
4. The caller waited out its own three-minute timeout, killed the process, and
   learned nothing. Exit code 0 either way.
5. `taskkill` refused to end any of the four — "there is no running instance of
   the task" for PIDs that CIM listed as alive with real threads — so even
   clearing them by hand was not available.

Five separate things, and only the first is a bug in the ordinary sense. The
design failure is that steps 2 to 5 could all happen with nothing anywhere
saying so.

**Windows will not let a running image be overwritten. It will let one be
renamed.** Measured rather than assumed, and the measurement is the whole design:
a process copied to a temporary path and started keeps running perfectly after
its own executable is moved aside, because the mapping follows the file rather
than the name. A new file can then be written at the original path immediately.
The retired one stays locked until that process exits, which is the only part
that has to be tidied up later.

So `AppInstall.Replace` stages the new build beside the old one, renames the old
one to `ExplorerNative.exe.retired-<stamp>`, and renames the staged file into
place. The plain overwrite is still tried first, because it is what happens when
nothing is running and it leaves nothing to sweep. Nothing has to close, nothing
has to answer a pipe, and there is no moment where the path names nothing — which
matters more here than anywhere, since that path is the registered handler for
every folder on the machine.

**What a running instance keeps is the old binary**, and the report says so
rather than implying otherwise: *"A copy is still running from the previous
build. It will pick this up the next time it starts."* Asking it to restart is
now a courtesy the install attempts and does not depend on. `SweepRetired` runs
at the start of every install and again on every launch, so the leftovers go on
the first pass after whatever was holding them has gone.

**And nothing on a command-line path can block for ever any more.** Three rules,
all of them broken by the incident above:

- **The log is written first, always.** `%APPDATA%\ExplorerNative\install.log`
  has a line per run whether or not anybody was watching. A dialog is gone the
  moment it is dismissed and a console scrolls away; a file is still there
  tomorrow, which is when somebody asks why their change is not in the
  application.
- **The console is used when there is one — for a command-line run only.**
  `AttachConsole(ATTACH_PARENT_PROCESS)` borrows the console of whatever started
  this, which is what makes `-NoNewWindow` above report into the caller's window
  like any other tool. .NET has already decided its standard output goes nowhere
  by then, so the streams are re-opened after attaching or nothing appears.

  **It first shipped attaching on every launch, and that froze the terminal it
  was installed from, repeatedly.** A file manager attached to the console that
  started it holds that console for as long as it runs — and the copy the
  installer starts runs for ever. The installer also started it with
  `UseShellExecute = false`, so it inherited the terminal's standard handles as
  well, and anything waiting for that pipe to close waited indefinitely. Closing
  the terminal was the only way out, and closing it sent the console's close
  event to the file manager too. `IsCommandLineRun` gates the attach now, and the
  relaunch goes through the shell so the new copy inherits nothing.
  `GetConsoleProcessList` from the terminal is how to prove it: the running file
  manager must not be in the list.
- **A notice closes itself.** When there is no console — a double-click — the
  message is a small window rather than a `MessageBox`, and it closes after
  `NoticeMilliseconds`. A MessageBox can only be dismissed by a person, and that
  is precisely the property that cost three minutes and told nobody anything.
  `--quiet` suppresses it outright for a caller that wants to be certain.

**And the exit code means something.** `Environment.ExitCode` is 1 on any
failure. It used to be 0 on every path, which is why the old procedure needed a
hash comparison bolted on afterwards to find out what had happened.

### Driving the running app from a script

There is no test harness for the UI. UI Automation works well enough to assert on
it — the status bar carries the current folder:

```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win  = $root.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition(
          [System.Windows.Automation.AutomationElement]::NameProperty, "Explorer Native")))
```

Two things to know. `Start-Process ... -Wait` on the executable **hangs** unless
an instance is already running — with none, the process you launched *is* the app
and never exits. And the first UIA child of the list is a `Pane` (the column
header), not a row; count from index 1.

## Shape of the code

| File | What it owns |
| --- | --- |
| `Program.cs` | Entry point, single-instance lock, `--register-default` / `--unregister-default` / `--unregister-all` |
| `SingleInstance.cs` | Named-pipe handoff of a folder from a new launch to the running app |
| `TrayApplicationContext.cs` | Tray icon, global hotkeys, the audio player, owns the form's lifetime |
| `MainForm.cs` | The window: two panes, virtual `ListView`, navigation, file actions |
| `NameRules.cs` | Pure rules — typed-name validation, list ordering, command-line path repair |
| `TypeAhead.cs` | Multi-letter jump-to-name: the accumulating buffer and the match rule |
| `NavigationHistory.cs` | Which row to land on — the folder you stepped out of, or where you left off |
| `Settings.cs` | Persisted preferences, clamping, atomic save |
| `SettingsForm.cs` | Preferences dialog (category list + panels, deliberately not a `TabControl`) |
| `SettingChoices.cs` | Which numbers a settings drop-down offers, and why those |
| `AudioReport.cs` | What the player is doing, for the page that prints it — and no `Program` |
| `Notifications.cs` | Every message the application can produce, and whether it is spoken |
| `SpeechForm.cs` | That catalogue as a list, one chooser each: speak, or off |
| `Speech.cs` | NVDA controller client, on a background pump |
| `FolderSizeCalculator.cs` | Cancellable, bounded, cached folder-size walks |
| `FileOperations.cs` | Managed copy/move — used for conflicts needing a rename |
| `RoboCopyEngine.cs` | The normal copy/move path, driven by `robocopy.exe` |
| `ConflictForm.cs` | The one question a paste asks when the names are already there |
| `ArchiveFormats.cs` | Every format, what it is called, and which engine owns it |
| `ArchiveEngine.cs` | Compress and extract: the plan, the progress, the front door |
| `ZipEngine.cs` | Zip, written and read with every core |
| `TarEngine.cs` | The tar layer, over a file or over a pipe |
| `ParallelGZip.cs` | Gzip on every core, by writing it as a series of members |
| `BsdTar.cs` | 7z, xz, bzip2, zstandard and the read-only formats, via Windows' own tar |
| `ExtractionRules.cs` | Where each entry in an archive is allowed to land |
| `Crc32.cs` | The checksum zip and gzip are defined in terms of |
| `ArchiveForm.cs` | Name, format, how hard to squeeze — and nothing else |
| `ShellRegistration.cs` | Context-menu verb, startup entry, default-file-explorer takeover |
| `ShellContextMenu.cs` | The genuine Windows context menu, via `IContextMenu` |
| `ShellLink.cs` | A `.lnk` on disk, written and read through the shell's `IShellLink` |
| `FileLauncher.cs` | Opens a file without paying `ShellExecute`'s association cost |
| `AudioPlayer.cs` | The audio player: what happens, and what it says |
| `WasapiRenderer.cs` | The endpoint, the render loop, the gain, the limiter and the meter |
| `AudioDecoder.cs` | A file to float, through `IMFSourceReader`, at the device's rate |
| `TimeStretch.cs` | Speed without pitch — WSOLA, and a bypass at normal speed |
| `SilenceReduction.cs` | Silence reduction: a pull stage, so it can change how long the music is |
| `SpeedForm.cs` | The speed, as a list applied as you arrow onto it |
| `LimiterForm.cs` | The limiter's attack, release and ceiling, the same way |
| `SilenceForm.cs` | What counts as silence, how long a gap may be, and the two fades |
| `DeviceForm.cs` | Which speaker it comes out of, the same way |
| `WasapiPlayback.cs` | The ring buffer joining those, so the render thread never waits |
| `AudioActions.cs` | The one list of audio actions — shortcut, menu label, announcement group |
| `AudioTags.cs` | What a song says about itself, via the Windows property system |
| `AudioPrefetch.cs` | Warms the selected file through the OS cache, so Enter is quick |
| `StreamingSource.cs` | Plays a track through a download held in memory, so skipping is free |
| `OggPages.cs` | The Ogg page layer: find a page, bisect on granules, read packets |
| `OggOpusReader.cs` | Opus out of Ogg, one page at a time, with exact seeking |
| `OggVorbisPackets.cs` | Vorbis packets for NVorbis's `StreamDecoder`, placed on the timeline |
| `ExactSeek.cs` | Where Media Foundation's seek cannot be trusted, finds the frame itself |
| `FlacSeek.cs` / `Mp3Seek.cs` | The two formats that needs: frame bisection, and a background frame walk |
| `VirtualStream.cs` | A header followed by the file from an offset, for reopening mid-file |
| `AiffStream.cs` | AIFF and AIFF-C presented to Media Foundation as WAV |
| `CdTrackStream.cs` | An audio CD track read off the disc and presented as WAV; Play CD, and a disc playing through |
| `SongPropertiesForm.cs` | That, as a list you can arrow through |
| `Shortcut.cs` | A key combination as text — `"Ctrl+Alt+P"` — parsed and formatted |
| `ShortcutBox.cs` | The Preferences field you set by pressing the keys |
| `HotkeyManager.cs` | Every global hotkey, on one message-only window |
| `GoogleDrive.cs` | Drive on a drive letter: mount, sign-in state, orphan sweep |
| `CfApi.cs` | The Windows Cloud Files API, and a layout check for every structure |
| `DriveMount.cs` | The provider itself — placeholders, population, serving bytes |
| `DriveFileCache.cs` | Downloaded audio in memory, chunked under one budget |
| `DriveClient.cs` | Drive v3: list a folder, read a range of a file |
| `DriveWrites.cs` | The half of Drive that can lose data: upload, move, trash |
| `DriveUpload.cs` | A tree to send: what it consists of, and four streams sending it |
| `GoogleAuth.cs` | OAuth for an installed app; token encrypted with DPAPI |
| `DriveLetter.cs` | `subst` through `DefineDosDevice`, removed on the way out |

## Why the executable is 2MB now

It was 152MB, and almost none of that was this application. Measured by
publishing without `PublishSingleFile` and adding the files up:

| | |
| --- | --- |
| **WPF** — PresentationFramework, PresentationCore, WindowsBase, XAML, printing | **45.9 MB** |
| everything else in the .NET Desktop runtime | 113.4 MB |
| **this application's own code** | **0.62 MB** |
| both audio codecs together | 0.51 MB |

It was `SelfContained`, so the whole Desktop Runtime shipped inside, WPF
included. It no longer is, at Conner's request (2026-09-17). The runtime comes
from `C:\Program Files\dotnet`, where the .NET 8 Desktop Runtime is installed,
and the file is **1.97MB**.

**Startup did not change.** Measured on 2026-09-17, alternating the two builds,
seven launches each of a flag that does nothing but write the licences:

| build | size | launch (median) |
| --- | --- | --- |
| self-contained | 152.5 MB | 180 ms |
| **runtime installed** | **1.97 MB** | **183 ms** |

**The trade that was taken.** This is the shell's handler for every folder on
the machine:

- If the runtime is uninstalled, the launcher cannot start the app. It shows its
  own message offering the download, and it does so for every folder opened,
  until the runtime is installed again.
- `RollForward` is `Major`, so a newer .NET is used when 8 has been removed.
  Only no .NET Desktop Runtime at all breaks it.
- `--unregister-default` needs the runtime too. If folders stop opening and the
  runtime is gone, install the runtime first.

**Trimming is still not an option.** `PublishTrimmed` fails with NETSDK1175,
"Windows Forms is not supported or recommended with trimming enabled".

**And "launched every time a folder is opened" means the messenger, not the
application.** The application stays resident — tray icon, `CloseToTray`,
`StartWithWindows` — and what Windows starts per folder is a second copy that
takes the single-instance mutex, fails, writes the path down a named pipe and
exits. `Main` does the mutex and the handoff before loading settings, starting
speech or touching WinForms, so that launch is very nearly all .NET runtime
startup.

**What is not worth doing:** replacing the audio codecs. Concentus and NVorbis
together are 0.51 MB, and writing an Opus decoder by hand would save little and
cost a codec's worth of correctness risk.

## Settings that are code now (2026-10-05)

Conner removed these from Preferences and they are constants, not settings, at his request ("just hard code these values"). Do not bring them back as settings:
- No navigation announcements, and no size or type spoken on select.
- Name, Size and Modified columns. There is no Type column.
- Volume step 1, `VolumeCurve.Perceptual`, and holding a key repeats.
- Drive keeps 1024 MB, lets a file go after 120 s idle, and always warms a copied file.
- Drive is on whenever signed in; there is no checkbox.

Everything under %APPDATA%\ExplorerNative is DPAPI-encrypted (`ProtectedFile`). Read a log or settings.json with `ExplorerNative.exe --read-log <name>`.

## Google Drive monitor (folder sync, 1.0.5)

Pairs of a PC folder and a Drive folder, each with a mode (UploadOnly, DownloadOnly or TwoWay), a CopyDeletes box (off by default) and Paused.
- **Files:** `DriveSync.cs` has the model and the pure `SyncPlanner` (all the rules, unit tested). `DriveMonitor.cs` is the engine, owned by the tray, and `DriveMonitor.Current` points at it. `DriveMonitorForm.cs` holds the list, the add/edit dialog and the Drive folder picker.
- **Where things are stored:** pairs live in `Settings.DriveSyncPairs`. Per-pair last-sync state is in `%APPDATA%\ExplorerNative\drive-monitor\<id>.json`, written through ProtectedFile.
- **When it runs:** only while `GoogleDrive.Client` is non-null, which means mounted. It runs on a FileSystemWatcher (3 s debounce), on Sync now, when Drive mounts, and every 60 s.
- **How it works:** one pair at a time, through the API only. It never walks the drive letter or the sync root, and `DriveMonitor.Refusal` rejects those folders.
- **Replacing a Drive file:** it uploads the new copy, then trashes the old one. Downloads go to `name.partial` and then move into place with Drive's modified time. Uploads are stamped with the local modified time, so size plus time (with 2 s slack) is the change test.
- **Conflicts:** the newer copy keeps the name. The other is renamed `name (conflict yyyy-MM-dd).ext` on its own side, and the next pass copies it across.
- **Saving:** monitor dialog changes are saved at once through `DriveMonitor.SetPairs`, and Preferences' working copy is kept in step.

Resilience rules, at Conner's request, for syncs of hundreds of gigabytes:
- **Detection never depends on events.** Every pass lists both sides and compares them with the state, so changes made offline or while the app was closed are found. Watcher events and overflows only make a pass due. The pass is due on Sync now, at start, when Drive mounts, when `GoogleDrive.ConnectionChanged` reports Online, and on each pair's CheckMinutes timer.
- **State is saved as work completes** (every 2 s and in a finally). The planner is idempotent: a transfer finished but not yet recorded comes back as Record, not as a copy or a conflict.
- **Downloads resume.** They go to `name.partial`, `PairState.Partials` records which Drive file the part belongs to, and the download continues by ranged read when `SameDriveFile` (id, size, md5 or time) still holds. Before the rename into place, the size and md5 are verified.
- **Uploads resume.** The resumable session URI goes into `PairState.Uploads` through the `Upload(..., resumeSession, sessionStarted)` hook in DriveWrites. Sessions older than 6 days are dropped and started fresh.
- **Space is checked.** The PC's disk keeps `SyncSpace.Margin` (2 GB or 5%) before each download, and Drive's quota is checked before each upload. Running out stops the pass with a "paused: ..." problem, said once through sync.space, and the pair carries on by itself later. The add/edit dialog totals what would copy (`DriveMonitor.NeedsAsync`) and offers to save the pair paused if it does not fit.
- **Big passes announce progress.** Over 1 GB or 200 files, sync.start is said at the start and sync.progress at 25, 50 and 75 percent.

1.0.6, for a first sync over two copies of one library (hundreds of gigabytes on both sides), nothing is re-transferred:
- **Adoption.** On both sides with no state (or changed on both): `SyncPlanner.Adoptable` compares size and then Drive's md5Checksum with the local MD5. Without an md5 on Drive it uses size plus mtime within 2 s. A match is Record; a mismatch follows the clash rule; an unknown hash leaves the file alone that pass.
- **Hashing.** `DriveMonitor.CheckWhatIsThereAsync` hashes what `NeedingHash` asks for, 2 at a time at BelowNormal priority, cached in `PairState.Hashes` by path, size and mtime (`SyncHashes.Split`).
- **State lifetime.** The state belongs to the pair id and records its two folders (`StateFits`). Editing options keeps it; a changed folder clears the sync records, and the hashes survive while the PC folder is the same. It is deleted only with the pair.
- **Rate limits.** `Patiently` wraps every Drive request in the engine. `SyncBackoff.IsTransient` (429, 403 rate limits, 5xx, a daily limit) waits for ever: 2 s doubling, capped at 5 min, jittered, with Retry-After honoured (`DriveClient.LastRetryAfter`). It never fails or skips a file; the episode is announced once via `sync.ratelimited`.
- **The configurator is File > Google Drive sync configurator**, visible only while `GoogleDriveEnabled` (`DriveMonitorForm.OnFileMenu`).

The account button reads `SettingsForm.AccountState`, which the tray sets: Connect, Disconnect (Connected) or Reconnect (NeedsSignIn). Disconnect unmounts and switches Drive off, but keeps the sign-in.

## The web app (1.0.6, 1.0.7)

Explorer Connect is a web app, and it is the only client: the iPhone app and its repository were removed (2026-10-05, Conner's decision). Nothing has to stay compatible with anything but web\app.js. The files live in web\ (index.html, app.js, app.css, sw.js, manifest, icons), embedded as web/<name> and served by ConnectServer through WebAssets at "/", "/app/...", "/sw.js" and "/manifest.webmanifest", without the pairing code. These are the app shell only; the data is under /api/.
- **The contract is what app.js calls.** Grep it for `/api/` before removing or changing a route. 1.0.7 removed ping, stat (the web app uses `/api/details`, because tracker blockers kill `/stat?`), the clipboard long poll and the clipboard history. `/api/info` (the Retry check) and the one-request `/api/upload` (files up to 4 MB) stay because the web app uses them.
- **ConnectServer listens on 127.0.0.1 only**, on `Settings.WebAppPort` (47810 by default, 1024 to 65535). Nothing on any network reaches it directly; the web app arrives through Tailscale Serve, which connects from this machine. `Rebind(port)` moves the listener without losing jobs or uploads; a port that cannot be had keeps the old one and says why (`ListenProblem`).
- **The port is checked on OK** (`ConnectServer.CheckPort`: range, `GetActiveTcpListeners`, a test bind on loopback; the port in use now counts as free). At startup a saved port that has been taken falls back to 47810, saved, and said once (`webapp.port.fallback`).
- Plain JavaScript, no libraries, no build step. The audio element is the only audio path (no Web Audio graph), so iOS has the best chance of playing in the background. There is no EQ.
- HTTPS comes from Tailscale Serve: `tailscale serve --bg --https=443 http://127.0.0.1:<port>` (TailscaleWeb). Never Funnel.
  - Enable reads `tailscale serve status --json` first and refuses if 443 is used by anything but our handler (`ParseServe`). Our handler on the previous port, or on 47810, is `OursOtherPort` and is moved. Disable removes only ours.
  - If HTTPS is off for the tailnet, serve prints a login.tailscale.com/f/... link. Preferences shows it as Enable HTTPS in Tailscale and retries every 10 seconds.
- Auth: Serve connects from loopback with Tailscale-User-Login. `ConnectServer.TrustsServeUser` accepts that header only when it equals this PC's own login, read from `tailscale status --json` at most every 30 s (`RefreshOwnLogin`); Tailscale stopped leaves the login unknown, which trusts nobody without the code. Everyone else on the tailnet sends the pairing code (header, or code= for media). `/api/whoami` (no auth) tells the page which. The pairing code is shown on Preferences, Web app; the tray's Web app item opens that page.
- Preferences, Web app: the status box re-checks every 4 seconds while Preferences is shown (never in tests, which build the form without showing it). The box acts at once; Cancel puts it back. `TrayApplicationContext.ReconcileWebApp` makes Serve match the setting at start and on change. Copy link is always there once the link is known; off, it says why instead of being disabled (a disabled button leaves the tab order).
- **Errors end in a sentence.** Every fetch in the web app has a time limit (`timedFetch`, AbortController). A PC that cannot be reached is the offline screen with Retry; a code that is no longer right is the code screen; 502/503 from Serve with no JSON means Explorer Native is closed. Uploads, jobs and playback pick up again on the browser's `online` event. `unhandledrejection` and `error` are announced rather than lost. On the PC side every `async` event handler in Preferences and the sync configurator has its own try/catch.

## Rules this codebase lives by

**A setting with one right answer is not a setting.** Preferences is walked one
control at a time by keyboard and read out one control at a time by a screen
reader, so every row in it is paid for on the way past every other row. Nine were
removed at once and each was the same shape — a number or a tick box whose value
had been *measured* into place, offered as though it were a matter of taste:

| Was | Is |
| --- | --- |
| Restore the window's size and position | Always. A window comes back where you left it |
| Maximum folder-size depth to walk | 64. Shallower does not bound the work, it makes the answer wrong |
| Wait before repeating / repeat every | `HoldRepeat.Delay` 200ms, `HoldRepeat.Interval` 20ms |
| A held key takes bigger steps | Always. Without it the repeat rate is beside the point |
| Speed step (percent) | Gone; the speed keys walk `AudioPlayer.SpeedLadder` |
| Download the track into memory | Always. Off was a way of making skipping slow again |
| Most to hold for one track | 1GB, for the pathological case |
| Only read ahead on network drives | Always. Locally it competes with the disk for nothing |
| Skip the system's audio effects | Always asked for; `RawMode` still reports what was granted |

The test for whether one belongs is not "could somebody want this different?" —
somebody could want anything different. It is whether either value is *right* for
somebody. A control offering a wrong answer and a right one is not a choice, it
is a trap with a label on it, and each of these also had to be clamped on the way
in from a file people are invited to edit.

The corollary, and it is the harder half: **the feature stays.** Every one of the
behaviours above still happens, at the value the setting used to default to.
Removing a control is not permission to remove what it controlled.

**And a setting that survives has to offer numbers somebody would pick.** Every
number in Preferences is a drop-down list rather than a spin box, for good
reasons — a list cannot hold a value that was never offered, and a screen reader
announces a combo box with its value and its position where a spin box announces
an edit field and leaves you to find the bounds by hitting them. But the list is
only as good as the values in it, and for a long time it was built from the
*width* of the range: one flat step, worked out once and applied across the whole
of it.

"Give the memory back after paused (minutes)" runs 0 to 1440, so the step came
out at 100 and the list was 0, 100, 200 … 1400, with the saved value of ten
inserted as a one-off in the middle. From the default the only reachable settings
were ten minutes and a hundred: five, fifteen, twenty and thirty could not be
expressed, and one press of the down arrow went from ten minutes to an hour and
forty. "Skip by (seconds)" was 1, 10, 25, 50. Most of the rest were the same, and
what it was reported as is "the combo box is broken" — which is the right word
for it.

`SettingChoices.StepAt` steps by the *value* instead, because round numbers are
round at their own magnitude: nobody wants 325 minutes and everybody wants 15.
The lists come out longer — 183 entries for the widest — and every entry in one
is a number a person would choose, which is the trade worth making when a typed
digit jumps straight into a drop-down.

It is a file of its own and public **because it was wrong for a long time in a
way nothing could see.** It was a private helper inside `SettingsForm`, and that
file was not compiled into the suite at all, so the one part of the dialog that
is pure arithmetic — and the part deciding whether any of its controls are usable
— was the part with no test on it. Arithmetic that shapes a UI does not get to
hide inside the UI.

**And `SettingsForm` is in the suite now, over one line.** It could not be,
because it read three diagnostics straight off `Program`, and `Program` owns
`Main` — a project that compiles it has an entry point of its own. So the largest
hand-built window in the application, nine pages of controls every one of them
constructed in code, had no test that it so much as opens, while the two small
dialogs beside it did. `AudioReport` is three delegates and a fallback; Program
fills them in on the way up and the page has no idea the entry point exists.

The test builds the dialog against a settings object with everything moved *off*
its defaults, because a window tested only against defaults is tested against the
one case that cannot be wrong. It also opens it on each start category, including
one that does not exist. This is the check that a page removed, a control
removed, or a setting renamed has not left the dialog throwing on the way up —
which for a window opened with Ctrl+P means a crash dialog instead of
preferences, and no way to change anything at all.

**Nothing slow happens on the UI thread.** Enumeration, folder sizing, deletion,
existence checks on network paths, launching a file, and speech all run on
workers. A network share whose server has gone away blocks `Directory.Exists` for
seconds; that is exactly the moment the window must not freeze, so use
`DirectoryExistsAsync`, never `Directory.Exists`, on anything user-supplied.

**Nothing waits on a share before the window exists, either.** The same rule one
step earlier. `MainForm`'s constructor chose its starting folder with a
synchronous `Directory.Exists` per candidate, and one of those candidates is
wherever you were last — routinely a share. This application is the shell's
handler for every folder on the machine and is started afresh each time one is
opened, so a NAS that had gone to sleep did not make the window slow to fill in:
it stopped the window appearing at all. `FirstExistingFolderAsync` does the
choosing on the way into `InitialiseTabsAsync`, through the same bounded probe
navigation uses, with a shorter ceiling because nobody asked for that folder and
the home folder behind it is always there.

**And nothing waits for a download to stop, either.** `StreamingSource.Release`
and `Dispose` both joined the filler thread for up to two seconds, and both are
reached from the thread that starts and stops playback — Dispose on every change
of track. One slow read from the share and the window froze at exactly the moment
the player is slowest. The join was not decoration: without it a Release
followed by a Resume could leave the old thread filling the new buffer from its
own stale offset. A generation number the old thread cannot change settles that
with nobody waiting; it re-reads it under the lock before touching the buffer
and before publishing what it read.

**Speech is queued, never called inline.** `Speech.Speak` enqueues; a background
pump makes the RPC into NVDA. Both `nvdaController_speakText` and
`testIfRunning` are synchronous calls into another process. `Speak` is reached on
every arrow press, so calling it directly ties the list's speed to NVDA's.

**The list is virtual and must stay that way.** The `ListView` holds no items;
`RetrieveVirtualItem` builds rows on demand from `Pane.Entries`. Two consequences
that have each caused a bug:

- The rendering cache is **bounded** (`MaxCachedItems`). An unbounded one keeps a
  `ListViewItem` alive per row ever painted, which is the thing virtual mode
  exists to avoid.
- `ListView.Items[i]` returns *our* cached object, not something the control
  owns, so setting `.Focused` on it does nothing. Selection and focus go through
  `LVM_SETITEMSTATE` (see `SelectIndex`).

**Anything proportional to the selection must be coalesced.** Arrowing fires two
selection changes per row, and bulk selection fires one per item. Totalling the
selection on each of those is quadratic. `_suppressStatus` brackets bulk changes;
`ScheduleStatusUpdate` coalesces the rest. If you add work to a selection
handler, it goes behind one of those.

**Announcements must not be eaten by the status bar.** `Announce` and
`AnnounceOperation` go through `ShowStatus`, which cancels the pending coalesced
update. Setting `_statusLabel.Text` directly will flash and disappear.

**An `async void` that throws takes the window with it.** There is no caller to
catch it, so it goes to `Application.ThreadException`, which writes the crash log
and puts a dialog up. The window's command handlers are nearly all `async void`
because that is what an event handler has to be, and most of them are safe only
because what they await — `NavigateAsync`, `RefreshAsync` — catches everything
itself. That is a property of those methods, not of the handlers, and it is worth
checking before adding a new one rather than assuming.

Two that were not covered by it have been guarded: `PasteClipboard`, where the
setup *before* `RunTransfer`'s own try can throw (this file already worries about
"a dialog that throws on its way up", one rule down), and `SongPropertiesForm`'s
`Shown` handler, where a properties sheet that could not read its tags ended the
session instead of showing what it had.

**Cancel a `CancellationTokenSource`; do not dispose it.** Background walks and
enumerations hold tokens taken from it, and linking a token from a disposed
source throws on a thread with no one to catch it. These sources hold no
unmanaged handle. This applies in `MainForm.BeginPaneWork` and in
`FolderSizeCalculator.Dispose`.

**One filesystem scan, not three.** `EnumerateFileSystemInfos` carries
attributes, length and timestamp out of the scan that found the entry.
`Directory.EnumerateFileSystemEntries` followed by `File.GetAttributes` and
`new FileInfo(x).Length` asks three times for what one call already returned —
three round trips per file on a share.

**A row is a sentence, and every column is a word in it.** A screen reader reads
each column in order, so the row is literally what is heard on every arrow press:
*"Local, modified 4 September, 1 of 3"*. The bar for a column is therefore not
"is this useful?" but "is this worth hearing on every single keystroke?".

The Type column failed that bar and is off by default. It spoke "Type Folder" or
"Type FLAC file" to say what the name had already said — the extension is right
there in it. Anything added to a row has to clear the same bar.

**Say less, not more.** Three separate things already speak when you move: the
row itself (NVDA), its position (NVDA), and anything the app announces. Entering
a folder used to announce its name over the top of the row that had just been
focused, which is why entering a folder announces nothing (it is no longer a setting). Ctrl+Shift+A says
where you are on request. The same reasoning keeps position-in-list off: NVDA
already says "3 of 47" at the end of a row, and a second copy is only an echo.

**Renaming a control is an announcement, so never rename the focused one.** The
name of the focused object changing is an event a screen reader reads out, and
it is not an announcement the application can be seen making. The panes are
named after the folder they show, and navigation set that name on the way into
every folder — while the list was already focused, every time — so opening a
folder spoke the folder's name over the row that had just been focused. Which
is the thing the navigation announcement (now removed) used to cause: the setting was
working and something else was talking, for the second time in this file. The
name is held in `Pane.PendingName` and applied on the list's `Leave`, where
changing it is silent, and a tab switch still names the pane *before* moving
focus to it. A test reads `MainForm.cs` and enforces it, because the rule lives
nowhere the compiler can see it.

**Entering a folder says the first row, and the application is what says it.**
Not the screen reader. In a virtual list an item's identity is its index — there
is no object behind row 0, only the number 0 — so landing on row 0 of a new
folder while row 0 was already focused is not a focus change to anything watching
from outside, however completely the contents were replaced. Measured with NVDA's
own speech log: descending six nested folders produced six keystrokes and not one
word.

Three cleverer answers were tried and all three are recorded here so they are not
tried again. Predicting when the control *would* raise its own event, from the
landing row: wrong, silent. From whether the list grew or shrank: wrong the other
way, the name twice. Raising the focus event explicitly with
`AccessibilityNotifyClients`: works in some folders and not others, because the
reader de-duplicates by that same unchanged identity and drops it exactly when it
is needed. So `readRowAloud` is simply always true on arrival. Where the reader
does announce as well you hear the name twice; the alternative is not hearing it
at all, which is the one somebody actually reported.

**Say the count, and only above one.** `NameRules.SayCount` is the rule: one item
is just the verb — "Copied" — and two or more says how many. Both halves matter
for the same reason and in opposite directions.

"Copied file" named the thing a second time. The row had been read out a moment
earlier, name and all, so the type was the least informative word available and
it stood in front of the one word that was doing any work. It was also *looked up
in the pane* rather than asked of the filesystem, deliberately, because asking
cost a round trip on a share — which is a good sign that the word was not worth
having.

Above one, the number is the entire message. A selection is the one thing that
cannot be checked by ear: the rows were read as they were picked and nothing ever
says how many there are now. "Copied 12 items" is the only confirmation that the
twelve you meant were the twelve you got, and it is the difference between
noticing a wrong selection at the copy and noticing it after the paste.

The noun is a parameter because the units differ — a transfer counts files and
failures separately, and calling both "items" would report a folder of three
thousand files as one thing. And the catalogue already described this shape
before the code did (`clip.copied` is `"Copied {0} items"`), so a test renders the
template and compares it against the rule: a sentence that no longer matches its
own example is how the Preview button starts lying about what will be said.

**Extending a selection says nothing by itself either.** A plain arrow moves the
focus and the reader reads the row, but Shift+arrow and Ctrl+Space are silent:
the control reports those as a change to a *range* rather than a change of focus,
and a range change is not something a reader announces. Building a selection by
keyboard was silent from the first key to the last, with the selection correct
throughout and no way to hear any of it. `select.count` is raised for exactly
those keys — never for a plain arrow, which would be an echo — and it is the one
entry in its group that is on by default.

**Focus the control, then the row — in that order.** A row focused in a control
that does not yet have the keyboard announces nothing; the control is then
focused, and all a screen reader has to report at that instant is the list
itself, so arriving in a folder said "Conner, 7 items" instead of naming the file
you landed on. `FocusListAndRow` focuses the list and *then* re-applies the row,
which produces the event that names it. Entering a folder should read the first
item, not the folder.

**A window with a fixed height will clip a field on somebody else's machine.**
`ProgressForm` was 560x340, which fits seven rows and a button at 100% scaling
and does not at 150%: "Time remaining" was covered by the Cancel button. It was
still built, still updated on every tick and still reachable with Tab, so a
screen reader read it out perfectly while nobody looking at the window could see
it — the one combination that is hard to notice from either direction, because
checking the announcements finds nothing wrong and so does reading the code.

`OnLoad` measures `PreferredSize` and grows the window to fit, rather than adding
a bigger guessed number to the guessed number that was already wrong. The test
scales the form's font by 1.5 before showing it, because the suite runs at 100%
where the bug does not reproduce — without that it passed on the broken build.

**A transfer is not modal, and nothing about it ever needed to be.** A paste put
`ProgressForm` up with `ShowDialog` and the file manager was gone until the copy
finished — which for forty gigabytes to Google Drive is most of an afternoon of
not being able to look at a folder, play a track, or start a second copy. The
transfer already ran on its own thread and reported through a callback; all
`ShowDialog` contributed was a disabled main window.

It is `Show()` now, unowned, `ShowInTaskbar`, one window per transfer, cascaded
so several do not stack in one place, and titled with the destination
(`TransferTitle`) because three windows called "Copying" is a taskbar you have
to click through. **Unowned deliberately**: Windows keeps an owned form out of
Alt+Tab, which for the only window with the Cancel button on it would be worse
than modal. Escape and the close box both cancel, and the window stays up saying
"Cancelling" until the transfer has really stopped — one that vanished while
robocopy was still finishing a file would have lied about what it did.

Four consequences, and three of them were new bugs:

- **The suppression is per-folder now, not global.** `_suppressWatch` stops the
  watcher for the whole window, which was harmless while a transfer held that
  window modal and nobody could be looking at anything. An hour-long background
  copy makes it an hour in which no folder anywhere notices anything — a file
  arriving from another program, a rename done in Explorer, none of it.
  `_transferTargets` counts transfers per destination and `ApplyPendingRefresh`
  skips only that folder. Same raise-and-release-in-a-finally rule, same
  consequence for getting it wrong, and the suite counts them.
- **The refresh at the end is of the destination, not of whatever is on
  screen.** `RefreshAsync` was right when the window was modal, because what was
  on screen could only be the folder the paste happened in. A copy that takes
  twenty minutes finishes with somebody three folders away, and rebuilding
  *their* list underneath them — cursor moving as it goes — is the modeless
  version of the same call being wrong. `RefreshDestination` reloads it if it is
  in view and otherwise marks the pane, which is exactly what the watcher does
  for a change made by anything else.
- **Closing, quitting and restarting all have to ask.** All three were
  unreachable while a transfer held the window modal. All three kill a copy
  where it stands, and for a *move* that means files deleted from one end and
  not yet written to the other. `ConfirmAbandonTransfers` is the one question,
  asked from `OnFormClosing`, from `RestartApp` — before the replacement is
  launched, or answering "no" leaves two processes fighting over the lock — and
  from the tray's Exit.
- **But never on the exit command from another launch.** That path is how
  `--install-only` asks the running instance to stand down, and a modal question
  there is an installer waiting out its thirty seconds against a dialog nobody
  is looking at, then reporting that the file is in use. `Quit(ask: false)` is
  the default for that reason.

The ProgressForm variable is called `window` in both transfer methods, where
every genuinely modal dialog in `MainForm` calls its own `dialog` — a test reads
the source and fails on `window.ShowDialog`, because `ShowDialog` is what every
*other* dialog in this application correctly uses and is exactly what a later
edit reaches for out of habit.

**A report that arrives before the window exists is kept, not dropped.** The
transfer starts before `ShowDialog` does — deliberately, so nothing waits on a
window — and its first report is the one carrying the totals the whole dialog is
about. `Update` returned early on `!IsHandleCreated`, so the dialog opened
reading "Starting" and "calculating" until the *second* report arrived, which for
an upload is a Drive round trip away. It is held in `_pending` and drawn on
`Shown`, and the caller hands it over directly rather than through `BeginInvoke`,
which has nothing to invoke on yet.

**And "Cancelling" is not overwritten by the ticks still arriving behind it.**
Cancelling is not instant — an upload has to abandon whatever requests are in the
air — and every one of those reported its progress afterwards, putting "Working"
back over the one word that said the button had been heard. Pressing Cancel and
watching the dialog go straight back to "Working" reads as a button that did
nothing, which is the moment somebody presses it again or starts closing things
by hand.

**Progress is throttled where it is produced, not where it is drawn.** Four
upload streams each reporting every quarter megabyte is thousands of cross-thread
posts a second, all to draw a number that changes ten times faster than anybody
can read it — and the thread they queue on is the one that has to draw the
result. `DriveUpload.ReportMilliseconds` is 100, with a forced report at the
start, at the end, and as each file lands, because those are the moments where a
hundred milliseconds of staleness shows the wrong file's name against a finished
count.

**Ask for text with `PromptForm`, never `Interaction.InputBox`.** The VB dialog
has no association between its prompt and its field, so a screen reader landing
in the box has nothing to say about what the box is for. `PromptForm` makes the
label the field's accessible name, focuses it on show, and selects the stem
(not the extension) when renaming or naming a file.

**Never move focus twice for one action.** Selecting the first row and then
correcting it to the right one is two focus changes, and a screen reader reads
both — so a refresh announced a row nobody asked for before announcing the right
one. `SetEntries` takes the row to land on and makes one move.

**Focus must always come back to the list.** Anything modal — Preferences, a
properties sheet, an input box, an error — leaves focus on the *form* when it
closes, and the form is a place where no key does anything: no arrows, no
type-ahead, nothing to read. Paths that end in a refresh get this free; the rest
call `RestoreListFocus`.

**A hidden control has no window handle, and selection lives in the handle.**
Selection and focus in a virtual `ListView` are state of the native control, so a
pane that has never been shown has nowhere to put them — the second tab was
loaded, had its row chosen, and still came up with nothing selected the first
time you switched to it. `OnHandleCreated` forces a handle for both panes.

**Landing on row one is a cost, not a neutral default.** Without sight, being put
at the top of a folder you have already worked through means listening your way
back down. Navigation therefore restores position: stepping up lands on the
folder you came out of (`NavigationHistory.ChildOnPathTo`), and returning to a
folder lands on the row you left (`FolderPositionMemory`, per pane). Refresh and
restart go through the same mechanism.

**The arrow-key path must not build a ListViewItem.** `ListView.FocusedItem` looks
like the way to ask which row is focused; in virtual mode its getter *constructs
the item* — raising `RetrieveVirtualItem` and formatting every column — so the
caller can read `.Index` and discard the rest. `FocusedIndex` sends
`LVM_GETNEXTITEM` and gets an integer. Same for `CurrentIndex`.

**The list controls are double-buffered and themed by hand.** WinForms sets
neither. `LVS_EX_DOUBLEBUFFER` stops a held arrow key turning into a flicker of
partial repaints, and `SetWindowTheme(handle, "Explorer", null)` selects the same
drawing path File Explorer's own list uses. Both are applied in `OnHandleCreated`;
anything that recreates the handle loses them.

**Measure before optimising, and check the harness first.** `SendKeys.SendWait`
paces at about 32ms per key — the keyboard auto-repeat rate — so wall-clock
throughput measured that way is the harness, not the app; Notepad measures the
same. Compare *CPU per row* instead, and against File Explorer on the same folder
with the same screen reader running. `PerfCounters` (set `EXPLORERNATIVE_PERFLOG`
to a file path) counts keystrokes, selection changes, `RetrieveVirtualItem` calls,
cache hits and build time. It is how we learned that the managed side is already
free: 100 arrow presses cost ~990 retrieve calls, all cache hits, ~0ms of build
time. What remains is the WinForms accessibility bridge serving the screen
reader, which cannot be reduced without replacing the list control.

**The folder on screen is watched, not polled.** A `FileSystemWatcher` per pane
marks it dirty and a 600ms settle timer re-reads it. The app's own commands
already refreshed when they finished, which covered its own commands and nothing
else: anything done through the Windows context menu completes *after* the shell
hands control back, so refreshing there was always a moment too early, and a file
written by another program never appeared at all.

Three things the design depends on:

- `_suppressWatch` is raised around our own copies and deletes. A paste of ten
  thousand files fires an event per file, and the refresh at the end of the
  operation is the one that is correct. **It is lowered in a `finally`, always.**
  Releasing it on each ordinary exit path instead covers neither a dialog
  throwing on its way up nor an announcement failing in between, and a counter
  left raised means the watcher never refreshes anything again for the life of
  the process — with nothing to connect that back to the copy that went wrong.
  `UploadToDrive` had that bug, had it fixed, and the same shape sat untouched in
  `RunTransfer` next door for the far more common path. A test counts the raises
  against the releases: releasing on several paths shows up as *more* releases
  than raises, which is exactly what it looked like.

  **And a `finally` only covers what is inside the `try`.** `DeleteSelected`
  raised the counter, and set `_deleting`, three statements before its `try` —
  with an announcement in the gap. An announcement that throws there leaks both:
  the folder watcher never refreshes anything again, and Delete never works again
  either, for the life of the process, with nothing on screen to connect either
  to the delete that went wrong. The raise goes on the last line before the
  `try`, with nothing between them.
- Only the *active* pane is re-read. The other is left marked and refreshed when
  it is switched to — navigation is written in terms of the active pane, and
  rebuilding a list nobody is looking at is work for its own sake.
- `ArmWatcher` returns early when already watching that path. A refresh
  re-navigates to the same folder, so rebuilding the watcher each time would
  churn a directory handle per refresh and leave a gap where changes are missed.

**A directory''s last-write time does not see into its subtree.** The folder-size
cache was keyed on it with a comment claiming "a folder whose tree changed since
we measured it is stale", and that is not what the stamp means: it moves when
this folder''s own entries change, never when a file two levels down grows. A
folder measured once and then filled up deeper answered with the old number for
the rest of the session, silently. Re-walking to decide whether to walk defeats
the cache, so the two are used together — the stamp catches a direct change
cheaply and `CacheSeconds` puts a ceiling on how long a deep one can hide.

**Automatic folder sizes is expensive, by nature.** `FolderSizes = Automatic`
walks the whole tree under every folder on screen. On somewhere like
`C:\Program Files (x86)` that is minutes of disk work, and navigation measured
3.8 seconds against ~150ms for the same app with it off. It is cancelled on
navigation and it never blocks the UI thread, but it competes for the disk.
"On demand" plus Ctrl+Shift+S gives the same number when it is actually wanted.

**And it must never walk the Drive letter.** Enumerating a cloud directory that
has not been opened yet *is* a Drive listing — about 1.3 seconds each — so
Automatic asked for one network request per directory in the whole Drive, over
again on every navigation, and populated the tree as a side effect. That is the
exact thing the provider is built not to do, arriving through a setting on a
different page. `MainForm.CalculateAllFolderSizesAsync` skips anything
`GoogleDrive.Owns`; Ctrl+Shift+S still measures one, because that is somebody
asking rather than the application deciding.

**Type-ahead is ours, not the control's.** A virtual `ListView` owns no items to
search, so its built-in incremental search only ever managed one letter — typing
"sy" went to something starting with "s" and then something starting with "y".
`List_KeyPress` accumulates into `TypeAheadBuffer` and sets `e.Handled` so the
control does not also act on the keystroke. A lone letter (or the same one
repeated) cycles; a growing prefix refines and stays put while the current row
still matches. Both are what a Windows list has always done and what hands
already expect.

**A menu entry that can only ever say no is not on the menu.** A menu here is
read out one item at a time, so every entry on it is paid for on the way past
every other entry — the same arithmetic as a column in a row, one level up. An
entry whose only possible answer on this row is a refusal is a word heard on
every single opening of the menu in order to be useful on almost none of them.

Three commands are hidden rather than disabled, and a fourth deliberately is
not:

| | On the menu when |
| --- | --- |
| **Extract**, **Extract here** | the focused name is an archive (`ArchiveFormats.FromName`) |
| **Copy Drive link**, **Copy public Drive link** | the focused row is on Drive (`GoogleDrive.Owns`) |
| **Create shortcut** | the folder is not the Drive letter and not the drive list |
| **Compress** | always — anything selected can be compressed |

The Drive pair were on every menu over every row on the machine, including on
machines with no Drive mounted at all, where the two of them together could only
ever produce "that is not on Google Drive". `ShowDriveLinkItems` is the one rule
and `_driveLinkItems` is every entry that obeys it, on the popup and on the Edit
menu both — two menus applying the same rule from two copies of it is how they
end up disagreeing about the same file.

**Hidden, not disabled**, because a disabled item is still read out — it is heard
in full and then heard to be unavailable, which is worse than either. And the
decision has to be free: it is made while the menu is being drawn, on the UI
thread, so it is made from the name (`FromName`, never `Identify`) and from a
path comparison against the mount (`Owns`, which asks Drive nothing). A menu that
costs a read is a menu that hangs on a sleeping share.

**Hiding an item does not release its shortcut.** Measured, not assumed: a
`ToolStripMenuItem` with `ShortcutKeys` set still claims them while
`Visible` is false, so Alt+L goes on working from anywhere in the window and
`CopyDriveLink` answers for itself when the row is not on Drive. That is what
makes hiding safe on the Edit menu, where the item is also the only place the
shortcut is written down.

**And a shortcut is a file, so it cannot be made in the Drive letter.** A `.lnk`
written into the sync root by hand is invisible from the moment it appears —
the pane is built from the account's listing — and is deleted by the next mount,
which clears the root. That is exactly the fault New file and New folder each
had and each had fixed, and the fix there was to create the thing *in Drive*
instead. There is no equivalent for a shortcut short of uploading one, and a
Windows shortcut to a local path stored in a cloud account is not obviously
worth the machinery, so `CreateShortcut` refuses and `ShowOurContextMenu` does
not offer it. `ShellLink` writes the file through the shell's own object rather
than emitting the format, on an STA thread of its own — the object's server is
registered `ThreadingModel=Apartment`, and `IPersistFile.Save` is a file write
that may be going to a share.

## The audio player

### What the player is

**It has no window, and that is the feature.** A file manager you are listening
to should not need a second window that has to be found and focused before the
volume can come down. So the whole player is: Enter on an audio file, the Audio
menu, four pages in Preferences, and global shortcuts. There is nothing to
Alt+Tab to, nothing for a screen reader to get lost in, and nothing to close.

It follows that **every command answers with the sentence to announce** rather
than speaking for itself. The same key press has to reach the status bar when the
window is up and NVDA directly when it is not, and only the caller knows which —
see `TrayApplicationContext.SayAudio`.

**The player is silent.** Not quiet — silent. Every shortcut it has does
something you can already hear: the volume moves, the track jumps, the music
starts or stops. Saying it as well is narration over the thing being narrated,
and over the screen reader reading whatever row you were on. All five
announcement preferences (`AudioAnnounceStart`, `TrackEnd`, `Transport`,
`Volume`, `Seek`) therefore default off, and `Settings.Migrate` has turned each
of them off once for anyone carrying an older file.

Exactly two things speak regardless: **"say what is playing"**, which exists to
be asked, and **a command that could not run** — "Nothing is loaded" — without
which a key with no track behind it is indistinguishable from a broken one.

**Showing and speaking are separate.** `SayAudio(message, speak)` writes the
status bar either way and only reaches NVDA when the category is switched on.
A line nobody has to listen to costs nothing, and it is there for anyone looking
at the window. Do not collapse the two back together.

The switches are grouped by `AudioSay` rather than one per action, because the
useful question is "do I want to hear about the volume", not "do I want to hear
about volume up but not volume down".

**The Audio menu is built from `AudioActions.All`.** So is the shortcut page, and
so is the tray registration — an action added to that list appears in all three
without any of them being edited, and the shortcut the menu displays is the
shortcut that is actually registered. The menu is also the answer to "no window"
not meaning "no way to discover the feature".

Three rules in it:

- The labels for a toggle **change to what the next press will do** — "Pause"
  while playing, "Play" while paused — read fresh on `DropDownOpening`, because
  the player is driven by global shortcuts that fire while the window is not on
  screen and anything cached would be wrong more often than right.
- The two states of one toggle **share their access key** (`&Pause` / `&Play`),
  so the key does not move under the hand when the state changes.
- Every other access key is distinct. That is not obvious reading labels one at a
  time — "Speed u&p" next to "&Pause" both claim P, and a shared letter stops
  acting and starts cycling — so `AudioActions.AllMenuTexts` exists and a test
  checks it.

**`ShortcutKeys` is not used on the Audio menu items, `ShortcutKeyDisplayString`
is.** These are global hotkeys owned by the tray; letting WinForms claim them as
menu accelerators as well would give one key two owners.

There is no next or previous track. This plays a file, not a playlist; the list
of what to play next is the folder you are standing in, and it is already on
screen with the keyboard in it.

### How it plays: decode, stretch, render

**Media Foundation, because of FLAC.** The obvious routes — `mciSendString` and
`waveOut` — cannot decode it, and neither can anything else that ships without a
dependency. Media Foundation decodes whatever Windows decodes (MP3, AAC, WMA,
WAV, ALAC and FLAC) and `mfplat.dll` is already on the machine. Nothing was added
to the build to get it.

**The media engine is gone, and only the decoder is left.** `IMFMediaEngine` —
the object behind an HTML5 `<audio>` element — did the decoding *and* the
rendering, and for a long time that was the whole player. It was the right first
answer and the wrong last one. An engine that renders for itself offers exactly
one hook into its audio path, an MFT; on this machine that hook takes every
sample and never gives one back, so the player was silent with it inserted. And
nothing above full scale, no `AUDCLNT_STREAMOPTIONS_RAW`, and no meter of any
kind can be reached from outside it.

So the audio path is now four files, and the split is the point:

| | |
| --- | --- |
| `AudioDecoder` | file → float, via `IMFSourceReader`, at the device's rate |
| `TimeStretch` | speed without pitch |
| `WasapiRenderer` | the endpoint, the render loop, gain, limiter, meter |
| `WasapiPlayback` | the ring buffer joining them, so the render thread never waits |

`AudioPlayer` decides what happens and what to announce, and owns none of it.

**Speed without pitch was the one thing the engine did better, and it had to be
rebuilt before the engine could go.** Resampling changes the speed by changing
the pitch, which is a chipmunk; `TimeStretch` is WSOLA. Output goes out in hops
of 1024 frames and each hop consumes `1024 * rate` frames of input, which is the
whole of how the speed changes. The join between hops is what decides whether it
sounds like music — a *fixed* join butts two uncorrelated waveforms together and
produces a click at the hop rate — so each new segment is searched for within
±640 frames and the position that correlates best with what is already playing
wins. That search is the difference between "speeded up" and "broken".

Four things in it that are not decoration:

- **Rate exactly 1 is a bypass**, bit for bit. Normal speed is overwhelmingly the
  common case and must not pay for this or be coloured by it.
- **The correlation is one signal** for every channel — a mono sum, not channel
  zero. Correlating each separately slides them against each other, which is a
  stereo image that moves when you touch the speed control.
- **The fractional remainder is carried** between hops. Dropping it makes the
  speed wrong by a little on every hop, which over a track is wrong by a lot.
- **The end-of-track residue is emitted proportionally**, not wholesale. Dumping
  it plays the last fragment at normal speed however fast the track was set to —
  measured as 5.6% too much audio at 4x, which is exactly what the length test
  caught.

**It sounded speeded-up rather than good, and four numbers were why.** The first
working version passed every test in this suite and had the flanged,
underwater quality that speeded-up speech gets. All four causes are the same
kind of mistake — a parameter chosen to be cheap, in a place where cheap is
audible:

- **A 2048-frame hop is 43ms, which the ear hears.** A hop is a piece of the
  recording repeated or skipped, and at 43ms it stops being a change of tempo and
  becomes a short echo. 1024 is under that threshold and still long enough to
  correlate a bass note against, which is the other constraint: a hop shorter
  than a low period cannot find a join and warbles instead.
- **The overlap was a quarter of the hop**, so three quarters of every segment
  was butted straight on with no smoothing — a click at the hop rate, which is
  precisely the artefact the search exists to avoid, reintroduced a few hundred
  samples later. It is the whole hop now: 50 percent overlap, which is what WSOLA
  is normally written with, and every output sample is the sum of two windowed
  inputs rather than most of them being one raw input with a seam either side.
- **The crossfade was a straight line.** Linear is the right *gain* law for two
  correlated signals — which is what the search has just gone to some trouble to
  produce — but its corners are not: the slope changes instantly at both ends,
  and a discontinuity in the first derivative is heard, faintly, as the same
  periodic edge the fade was there to remove. A raised cosine is complementary in
  exactly the same way and has no corners.
- **The correlation looked at a quarter of the overlap**, on the grounds that a
  join which starts badly stays bad. True, and also how a join gets chosen that
  is perfect for four milliseconds and wrong for the twenty after it.

The last of those was affordable because the search got a coarse pass. A
normalised correlation over 1281 offsets and 1024 samples each is 1.3 million
multiply-adds per 21ms of output; the surface is a waveform against a copy of
itself, so it is smooth at that scale, and a pass every eighth offset (skipping
three samples in four) finds the right lobe with a second pass either side of the
winner finding the peak within it. Sixteen times less arithmetic than one
full-resolution pass, and **less than the old version spent on a quarter of the
overlap**.

**The test that matters for it is pitch, not length. And purity, not pitch.** A
resampler passes a length check too, so `TimeStretchTests` feeds a 440Hz sine
through and uses a Goertzel at 440Hz and at `440 * rate`, asserting the first is
at least four times the second — plus that the peak survives, because overlap-add
with a badly chosen join cancels against itself and fades in and out at the hop
rate.

Neither of those catches a stretcher that clicks, which is what the four numbers
above were doing: the clicks are quiet, broadband and periodic, and both tests
pass straight through them. A pure sine has exactly one place its energy can
honestly be, so the third test measures the tone's amplitude against the RMS of
the whole output — anything not at 440Hz was manufactured at a join. It reads
1.000 at 0.75x, 1.5x and 2x, and the threshold is set at 0.98 rather than at
something the current code merely clears.

**A speed change no longer hands the track anywhere.** It used to move the track
between two backends — reopen the file, carry the position, restore the play
state — and every one of those steps was a way for a speed key to lose the music.
It is now one line: `direct.Rate = percent / 100.0`. Verified in the running
application at 150% and 50%: the session stays active, the peak is unchanged, and
the clock speeds up and slows down.

**A COM interface is a list of offsets, so the declaration is the risk.** A
method declared in the wrong position calls its neighbour, with the right
arguments, and compiles perfectly. Two rules survive the engine's removal:

- Not every method returns an `HRESULT`. Anything handing back a `double` or a
  `BOOL` directly is `[PreserveSig]`.
- `BOOL` is declared as `int`, never `bool`. COM interop marshals a bare `bool`
  as a two-byte `VARIANT_BOOL` and reads the wrong half of a four-byte `BOOL`.

Deleting the engine deleted about three hundred lines of exactly this risk — a
42-method interface, then all 42 repeated in a 60-method extension, plus
`IMFAttributes` written out to put `SetUnknown` at slot 25. What is left is
`WasapiRenderer`'s device interfaces and `AudioDecoder`'s reader, and one slot
table worth remembering: **`IMFMediaType.SetUINT32` is slot 18, not 19.**
Declared one late it calls `SetUINT64`, which is accepted and then fails as
`MF_E_TOPO_CODEC_NOT_FOUND` (0xC00D5212) somewhere else entirely. The
declaration numbers every reserved slot in a comment for that reason:
`GetUINT32` 4, `GetGUID` 7, `SetUINT32` 18, `SetGUID` 21.

None of it can be reasoned about from the source, so `AudioPlayer.SelfCheck`
plays a real file and checks that what comes back is what went in — the duration,
the clock actually moving, pause and resume, and both speed directions. The suite
generates its files byte by byte: a silent WAV and a silent FLAC, the FLAC being
STREAMINFO plus constant-value subframes, which is how FLAC spells silence. A
test that needs somebody's music library is a test that passes on one machine.

**`SelfCheck` now asserts the clock moves.** That is not a small addition. A
player can hold a file open, report itself playing, answer every question
correctly and emit nothing at all — and the position sitting still is the one
symptom that separates it from a working one.

**And it seeks to the start before each speed measurement, or it is measuring how
long the fixture is.** The speed check runs last, after up to three seconds of
waiting for the clock to move and then a pause and a resume — and the FLAC the
suite generates is 1.95 seconds of audio. A track that has simply *finished* has
a clock that does not move, so the check failed intermittently as "the clock
stopped at 150 percent speed", with 150 getting there first because it is the one
consuming the remainder half again as fast. It was read as the stretcher and had
nothing to do with it. `SeekTo(0)` before each measurement, and a moment for the
pump to pick the seek up before the position is asked for. A test that fails on
its own fixture running out teaches people to re-run the suite until it is
green, which is the end of the suite being worth anything.

### Durations, and files Windows will not decode

**Windows reports a FLAC duration rounded down to the whole second.** 19.505
seconds of audio comes back as exactly 19 — and the Windows shell property
handler says the same, so it is the platform, not the file. WAV is exact.
`SelfCheck` accepts both.

**The duration arrives with the decoder, not with the play.** `AudioDecoder`
reads it from the source's presentation attributes when it opens, and the decoder
is opened on the pump thread — so there is a moment after `Play` returns when the
length is still NaN. Wait for it rather than reading it straight back.

This used to be worse and the old warning is worth keeping in view: the media
engine revised the duration *upwards* as it indexed a file whose header carried
none, so the first number was not the answer and the rule was "wait for it to
stop changing". Nothing does that now — the decoder either knows or does not —
but anything that goes back to a streaming source with no header will bring it
back.

**"Windows cannot decode .flac" was almost never about FLAC.** `Play` returns
null whenever `StartDirect` fails, and that happens when the device will not
open, when the file has gone, when a share is not answering and when the decoder
times out — all four of which came out as one sentence accusing the file format.
For somebody whose whole library is FLAC that reads as "this player cannot play
my music", and it is the least likely of the four explanations: Windows has
decoded FLAC since 10, and a probe against the actual failing file opened it in
186ms and reported 182 seconds of audio.

`WasapiPlayback.Diagnostic` already distinguished the cases — it prefixes
`renderer:` or `decoder:`, or says "the file did not open in time" — and `Play`
was throwing that away. It reports the real reason now. The lesson generalises:
a fallback message on a path with four causes is a wrong answer three times out
of four, and the one it names is the one people will act on.

**Ogg and Opus do not decode through Windows, so they are decoded here.** Measured:
`MFCreateSourceReaderFromURL` on a real `.opus` file fails with
`MF_E_UNSUPPORTED_BYTESTREAM_TYPE` (0xC00D36C4) in 33ms. The documented fix is
the Web Media Extensions, **which are installed on this machine** (2.1.38.0,
ARM64) — and it still fails, because that package registers its byte-stream
handlers inside the package where only other *packaged* applications can see
them. A plain Win32 desktop program gets nothing.

`HKLM\SOFTWARE\Microsoft\Windows Media Foundation\ByteStreamHandlers` is where
the difference shows: `.flac` and `.mp3` have entries, `.ogg`, `.oga` and
`.opus` have none. `AudioFiles.WindowsHasHandlerFor` reads it.

So there are two decoders now, behind `ITrackDecoder`, and `TrackDecoder.Open`
picks. **Media Foundation first, always** — it decodes everything the platform
has ever added, it is what this application chose in the first place, and none
of that changes. `OggDecoder` covers the gap, using Concentus (a managed port of
libopus) and NVorbis.

**This is the one exception to "no third-party packages", and what makes it
allowable is that both are pure IL.** No native binary, nothing
per-architecture, nothing that behaves differently on ARM64, and they bundle
into the single file like any other managed assembly — about 530KB against
152MB. The alternatives were shipping native codec DLLs per architecture, or
making this an MSIX app so the Store package becomes visible to it. Their
licences (Xiph BSD and MIT) are embedded alongside the NVDA client's, and all
three now come out of `--licence`.

The work in `OggDecoder` is not the decoding, which those two do. It is telling
the containers apart (Opus first — an `OpusHead` page is a cheap and certain
sniff — then Vorbis as whatever NVorbis will accept), matching the device's rate
and channel count, and keeping a position that means the same thing as
`AudioDecoder`'s, because `WasapiPlayback` talks to whichever one opened the
file and must not be able to tell which. Concentus ships a Speex resampler, so
the rate conversion is a real one rather than something invented here — and it
is only built when the rates actually differ, which for Opus at 48kHz into a
48kHz mix format is never.

`ComStream` is what lets an Ogg file on the Drive letter play out of
`StreamingSource`'s download like everything else: that class is a COM `IStream`
because Media Foundation wraps one, and the managed decoders want a
`System.IO.Stream`.

**The test generates its own Opus file rather than borrowing one**, because a
test that needs somebody's music library passes on one machine. It encodes a
440Hz tone, decodes it back and asserts the peak survives — a decoder that runs
and produces silence reports success from every angle except listening, which is
a failure this suite has shipped before.

**The Ogg container for Opus is read here, not by Concentus.OggFile.** Its
`OpusOggReadStream` constructor, given any seekable stream, reads and
checksums every page of the file and builds an object per packet before it
returns — to learn the length. On a 1.18GB, twenty-hour recording
(`Documents\LifeRecorder\Recording_30.opus`) that was five seconds to the first
sample, 465MB allocated and 411MB held while playing, and "back to the start"
rebuilt all of it. `OggOpusReader` reads the length off the last page, finds a
seek target by bisecting on granule positions, places each packet on the
timeline by working back from the granule of the page it ends on, and holds one
page. Same file: 8ms to open, 0.3MB, a ten-hour seek in a millisecond, and a
seek that lands on the sample (the suite compares two overlapping seeks sample
for sample, and checks the last two seconds come out as exactly 96000 frames).
Pre-skip is taken off the front and the last page trims the end, which the
library did neither of. The package stays: the suite writes its Opus files with
`OpusOggWriteStream`.

**Vorbis had the same fault in a different library**, and has the same fix.
NVorbis's container reader indexes every page the first time anything asks for
the length or a seek: 290ms and 68MB for an hour of audio, so `OpenVorbis`,
which reads `TotalTime`, paid it on every open (measured 388ms). The page layer
is now `OggPages`, shared by `OggOpusReader` and `OggVorbisPackets`, which is an
`IPacketProvider` handed to NVorbis's `StreamDecoder` — NVorbis still does all
the decoding. Same hour: 13ms to open. `VorbisReader`'s constructor that takes a
provider is marked obsolete and throws, which is why it is `StreamDecoder`.

`OggVorbisPackets.SeekTo` has a stricter contract than it looks. The decoder
reads one packet (no output, nothing to overlap), then one whose output must
*contain* the target, and skips in by target minus the granule returned. So the
packet is found by its own end granule, worked back from the page's using the
decoder's count delegate — which reads bits out of the packet, so every packet
it counts is `Reset()` before it is handed out.

`LongOpusRecordingTests` uses that recording itself rather than a generated
file — a generated twenty-hour file is a twenty-hour encode — and skips when it
is not there. As of 2026-09-16 the recording has moved out of
`Documents\LifeRecorder`, so it skips; `ExactSeekTests` covers Opus with a
generated file.

**An extension that cannot be decoded is not an error path.** The list in
Preferences is deliberately generous — Ogg and Opus are on it, and Windows only
decodes those if the Web Media Extensions are installed. When nothing can open
the file, `Play` returns null and the file is handed to whichever application
owns it, which is where it would have gone had the player been switched off.
That is what makes a wrong guess in the extension list cost a moment rather than
a file that will not open. It is also answered *on the spot* now, rather than
asynchronously through an error event well after `Play` had claimed success.

**Shift+Enter is the way past the player.** Turning the player on would otherwise
mean losing the ability to send a track to a real one, and this machine has
foobar2000 for a reason.

### Shortcuts, and the keys that may not be taken

**Shortcuts are stored as text.** `"Ctrl+Alt+P"` in settings.json, not a packed
integer, so the file stays something a person can read and edit — and so the
round-trip test covers all eight of them for free, since it already walks every
`string` property. `Settings.Validated` deliberately does not touch them: they
are parsed where they are used, and something unparseable costs a shortcut that
does nothing rather than becoming a shortcut nobody asked for.

**A global hotkey with no modifier is refused.** `RegisterHotKey` takes the key
away from every application on the machine, so a global shortcut of `P` means no
program ever sees a P again, this one included. `Shortcut.CanRegister` enforces
it. The media keys are the exception — they are already global by nature, and a
keyboard that has them is a keyboard where taking them is the point.

**And a modifier does not make a combination safe to take.** Ctrl+C carries one,
and setting "play or pause" to it would stop copying working in every application
on this computer — including in this one, whose own Copy would never receive the
key again. Nothing about that registration fails or warns, because from Windows'
point of view nothing went wrong: the combination was free, and now it is ours.
`ReservedShortcuts` is the refusal, and it is consulted from **both** ways in —
the box that captures the keys, and the registration that runs at startup —
because settings.json is a file people are invited to edit and
`Settings.Validated` deliberately leaves shortcut text alone.

It holds two kinds of entry, for two different reasons. The window's own keys,
because taking one globally means this application stops being able to do the
thing the key is for; those have to stay in step with the menu, so a test reads
`MainForm.cs` and counts them. And a handful that belong to nobody in particular
and everybody in general — Ctrl+Z is not ours and never appears in our menu,
which is exactly why it would be so quiet a catastrophe: undo would simply stop
existing, machine-wide, with nothing to connect it to a file manager's audio
preferences.

**The line it draws is "would losing this leave no way to do the thing".**
Not "is this key important", which reserves everything and takes away choices
somebody made on purpose. Extending a selection (Shift+arrow), toggling one item
(Ctrl+Space) and Space itself have no other keyboard route, so they are reserved.
Moving the cursor without moving the selection has one — the plain arrows — so
Ctrl+arrow is deliberately *not*, and that is not a principle arrived at in the
abstract: this list shipped with Ctrl+Up and Ctrl+Down on it and they were
already somebody's volume keys. Dump the real settings.json before adding
anything here.

**Every function key on its own is reserved, as a group.** F2, F5, F7 and F8 are
in the explicit list by name because the menu uses them; F1 to F24 are generated
in so the group cannot be half-listed. A function key is the one kind of key that
is *only* ever a command — nothing types F6 — so it never comes back the way a
letter does the moment Control is released. A bare F3 handed to Windows is Find
Next gone from every program on the machine for as long as this application runs,
with nothing on screen anywhere to connect the two.

That includes F13 to F24, which `Shortcut.IsSelfContained` still says Windows
will hand over with no modifier at all. The two are answering different
questions — "will Windows take it" against "should we ask" — and where they
disagree `ReservedShortcuts` wins, because it runs first. A function key *with* a
modifier stays usable: reserving 24 keys times 8 modifier combinations would take
away most of the shortcut space there is in order to protect nothing, since
Ctrl+Alt+F9 is not a command anything already has.

**A refusal that reaches the registration puts up a dialog.** Preferences will
not let one of these be typed, so anything arriving here got in by editing
settings.json — the one route where a silent refusal is worst. Somebody who has
just hand-edited a file to get a shortcut they were told they could not have will
read a dead key as the edit not having taken, and the natural next move is to try
harder, not to check the status bar. A status line is easy to miss and this is
exactly the moment somebody is watching for an answer.

`ShowShortcutDialog` is therefore modal, names the keys *and* what owns them, and
is raised **in addition to** the notification rather than instead of it — the
catalogue is still where the message belongs. It is posted through `_ui.Post`,
never shown inline: `RegisterHotkeys` runs from the constructor, before there is
a window to own a dialog or a message loop to pump one, and a modal window there
stops the application appearing at all until it is dismissed. A file manager that
looks like it failed to start, because a shortcut was wrong.

**It covers three refusals and one dialog says all of them.** Reserved, used
twice, and no modifier at all — every one of them a mistake the settings file can
express and Preferences cannot. The duplicate is the quietest and the most
misleading of the three: Windows gives a combination to one registration only, so
one of the two keys works perfectly and the other is simply dead, which reads as
"that action is broken" rather than "those keys clash" — and which of the two
survives depends on the order `RegisterHotkeys` happens to walk the list.

**One dialog, never one per problem.** Two posted together arrive about ten
milliseconds apart and the second opens *inside the first one's modal loop*, so
they stack. Measured with NVDA's speech log: the second was read out in full —
title, body, button — and the first was never announced as a dialog at all, while
still sitting underneath waiting to be found by somebody with no way of knowing
it was there.

`taken` — another application already owning the key — is deliberately left out
of the dialog. It is not a mistake in the settings file and it is not necessarily
true twice running, so it stays a notification. A modal dialog on every launch
because something else on the machine claimed a combination first is how a
warning teaches people to dismiss warnings.

**`EXPLORERNATIVE_HOTKEYLOG` is how this gets debugged.** Set it to a file path
and every registration writes down what it decided. It exists because this path
reports itself entirely through notifications and a dialog — which is to say,
through exactly the things that are not appearing when something here needs
investigating. It settled one such question in a single run: the trace showed
`reserved=3`, the dialog queued, the delegate running and no dismissal, which
said the dialog was up and blocking while UIA insisted no such window existed.

Verify it the only way that means anything, which is from outside the process:
call `RegisterHotKey` yourself and see who owns what. After an install, the
user's own combinations must come back **held** and every reserved one **free**.

### Seeking

**Never work out where to seek by asking the player where it is.** A reported
position does not move until the seek has actually taken effect — the media
engine's clock did not move until the seek completed, and the direct path's is
computed from what the ring still holds, so it lags a jump by whatever is queued.
Either way a burst of presses all read the same position, all compute the same
target, and all land in the same place. Ten presses of "skip forward" moved the
track forward once — the key felt dead after the first press, which is what
"skipping lags" turned out to mean. `Seek` counts from its own last target
instead, and falls back to the real clock only after two seconds of quiet, by
which time it has caught up and is the better answer.

That counting is the whole of it. The seek is applied on **every** press, at
once, with nothing coalesced or deferred — see the next rule for why the
throttle that used to sit here is gone.

**Skipping forward past the end starts the track again.** It used to clamp a
quarter of a second from the end, so a held key ran there and stayed: every
further press landed in the same place, and the only ways back into the music
were "back to the start" or rewinding by hand. Reported as "going to the end of
the track should start it over when I skip — it makes me start it over or go
back to the start". The wrap lands on zero rather than carrying the overshoot
round, because "it starts over" is what was asked for and a step that lands
eight seconds in has already missed the beginning. Backward past the start still
stops at the start: a held rewind arriving at the *end* of the track would be a
surprise, and nobody asked for it.

### Landing on the sample

**A seek plays exactly what a straight run to that moment would have played,
in every format, at the device's rate as well as the file's.**
`ExactSeekTests` decodes each file twice — straight to the target, and by
seeking — and requires them to agree sample for sample, and the clock to agree
too. It was nowhere near that. Measured on hour-long files before the fix:

| format | where a seek landed | what the clock said |
| --- | --- | --- |
| WAV | exact | right |
| AAC | up to 23ms early | right |
| WMA | up to 1.3s early | right |
| MP3, constant bitrate | ~20ms early | right |
| **MP3, variable bitrate** | **up to 5.6s early** | **the target** |
| FLAC with a seek table | **up to 10s early** | right, except before the first seek point |
| **FLAC without one** | **up to 35s late** | **the target** |
| Ogg Vorbis, Opus | exact | right |

And the clock was wrong for every Media Foundation format anyway:
`AudioDecoder.Read` set `Position` from each sample's timestamp and then added
everything written in the call on top, so it ran ahead by up to a buffer.

**Where Media Foundation stamps its landing truthfully, the difference is
decoded and thrown away** (`AudioDecoder.Discard`). The clock is a base time
plus a frame count, not a running sum of doubles — twenty hours of adding a
small double drifts by a sample.

**Where it does not — FLAC and variable-bitrate MP3 — the frame is found from
the file and the reader is reopened on it** (`ExactSeek`, `FlacSeek`,
`Mp3Seek`, `VirtualStream`). FLAC frame headers carry their own position under
a CRC-8, so it is a bisection, narrowed by the seek table; a candidate must also
match STREAMINFO and be followed by the frame it implies, because a sync code
turns up in audio by chance. The virtual stream is STREAMINFO (sample count
reduced, MD5 cleared) followed by the file from that frame. MP3 frames carry no
position, so `Mp3Seek` walks every header once on a background thread and keeps
every eighth offset; ten frames of run-up cover the bit reservoir. **Media
Foundation skips a first frame carrying Xing or VBRI and decodes one carrying
LAME's "Info" as a frame of silence** — measured as exactly one frame between a
VBR and a CBR file from the same encoder — and `Mp3Seek` numbers frames to
match.

**Only from bytes in hand.** Each search is dozens of small reads; on the Drive
letter one outside the download is most of a second. `StreamingSource` has
`Holds`, `ReadHeld` (memory only, for searching) and `ReadAt` (positional, for
the virtual stream, which must never move the decoder's shared position). A
FLAC search stays inside the downloaded part when the target is in it; the MP3
walk follows the download as it arrives. Past what has arrived, the seek is
Media Foundation's, as it always was.

**The resampler has a grid, and missing it is audible in a comparison though
not by ear.** Windows' resampler lays its output grid from the first sample it
is given, so a start that is not on both rates' grids plays the right audio a
fraction of a sample late — a difference of about 0.1 against a straight
decode. So every seek starts early, on a sample that is a multiple of
`ExactSeek.GridStep` (147 at 44.1kHz into 48kHz): FLAC and MP3 by choice of
frame; `OggDecoder`, whose Speex resampler has the same property, by its own
run-up; and Media Foundation's path by asking for a position that is on the grid
*and* on an AAC frame boundary, because AAC lands on the 1024-sample frame the
request falls in (`SeekRequest`). The run-up also covers AAC's first frame,
which decodes wrong without its predecessor, and the resampler starting from
silence.

**What is not exact, and why:**

- **WMA** stays up to about 1ms out (15–48 samples at 48kHz). ASF records
  presentation times to the millisecond, which is exactly that error; fixing it
  means parsing ASF. Not done, and the one format that is not exact.

Everything else is in `ExactSeekTests` and lands on the sample at 44.1kHz and
48kHz: WAV, AIFF, FLAC, MP3 (CBR, VBR, and files joined end to end), AAC, ALAC,
Vorbis, Opus (including chained and 5.1). Four things had to be learned to get
there, and each is worth knowing before touching a decoder:

- **ALAC** is written by hand in the suite (a minimal MP4 around verbatim ALAC
  frames), because Windows' own ALAC encoder crashes finalising. Its GUID is
  `616C6163-…` and its frames are 4096 samples; `AudioDecoder.FrameFor` needs
  both for the grid alignment.
- **AIFF** plays now. Windows has no handler for it, so `AiffStream` presents
  it to Media Foundation as a WAV — AIFF and AIFF-C, big and little endian,
  integer, float, µ-law and A-law — swapping bytes on read.
- **Media Foundation drops a frame after every join in an MP3** — a mid-file ID3
  tag, a run of zeros, junk between frames — and its output is short by the
  529-sample decoder delay. `Mp3Seek` numbers frames to match and its `Length`
  subtracts the delay; without both, joined files and durations were off.
- **NVorbis ends a long block followed by a short one (N−s)/4 samples later
  than the specification.** `OggVorbisPackets.TrailingShift` corrects the
  packet placement for it. Vorbis is tested from fixtures in `tests/Fixtures`
  (`clicks.ogg`, made with libsndfile), because nothing in the build encodes it.

And two the other way: an Opus seek needs at least 400ms of run-up to be bit
exact (500 is used), and chained Ogg files are bisected on serial numbers to
find the length.

**Anything on a timer must come back to the thread that owns what it touches.**
This was written for the media engine, a COM object created on the UI thread,
where calling it from a `System.Threading.Timer` was a cross-apartment call that
did not complete until that thread pumped. `AudioPlayer.Post` is how the volume
fade still gets home.

The rule outlived the engine because the shape repeats: the decoder belongs to
the pump thread and the device to the render thread, and both are reached the
same way — by handing work to the thread that owns them rather than reaching
across. What is gone is the *false negative* it used to produce, where a harness
measuring with `Thread.Sleep` instead of a message loop saw a seek never arrive.

**Never ask the Opus reader for zero.** `OpusOggReadStream.SeekTo(TimeSpan.Zero)`
looks for the last packet at or before granule zero, finds none — the first audio
page is already past it — and latches end of stream. Nothing takes that back:
`HasNextPacket` stays false for the rest of the reader's life, however far away
the next seek goes, and a reader drained to the end of the file cannot be seeked
back either. Measured against a real Opus file: seeking to 0 kills it, seeking to
one millisecond does not.

Three things reach that call and all three were the same silence — the track
still "playing", the clock stopped at zero, nothing coming out and nothing said:

* holding **skip backward** until it arrives at the beginning;
* **back to the start**;
* the **loop point**, which is how repeat is gapless — so an Opus track with
  repeat on went quiet the first time it reached its own end.

The fix was to rebuild the reader for that one target, which was "~17ms" on the
suite's six-second file and five seconds on a twenty-hour one — see Ogg and Opus
above. That reader is gone now: `OggOpusReader.SeekTo(0)` is the same bisection
as any other seek, with nothing to latch, and lands on sample zero.

The pump has the matching guard: a loop point that produces no audio is taken
twice and then the track is allowed to end. Without it, "turn the decoder round,
read nothing, turn it round again" is an infinite loop that makes no sound and
raises nothing — the worst possible way to report a decoder that cannot restart.

**"Playing" means sound is still coming out, not that the decoder is still
reading.** Those two disagree for any track shorter than the four seconds the
ring holds: it is decoded to the end almost at once, `_sourceDone` goes true,
and every note of it is still queued and audible. `IsPlaying` asked the decoder,
so pausing and resuming a short track answered "not playing" for a player that
was plainly playing — and it reaches the menu labels and "say what is playing"
by the same route. It was found as an *intermittent* failure of the player
self-check ("Play said it resumed and did not"), which is what a race between
the decode finishing and the check running looks like from outside. It asks the
ring now.

**At a loop point the decoder and the ring are measuring different passes.**
`Position` is the decoder's position less whatever is still queued, which is
right everywhere except across a repeat: the decoder has gone back to zero while
the ring still holds four seconds of the *end* of the track. The subtraction goes
negative, so the clock reads zero for the whole ring drain, and the last four
seconds of every pass are never reported at all. On the twenty-second Opus file
this was found with, the clock counted 0 to 16 and started again — "time
remaining" never went below four seconds and "time elapsed" never reached the
length, which is what "the length is not recorded properly" turned out to mean.

`_lastPassFrames` is the fix: the number of frames in the ring at the moment the
decoder turned round, counted down by `Fill` as they are played. While it is
above zero the answer is measured back from the end of the track instead of
forward from the decoder. It costs two integer operations on the render thread
and is zero at every other moment, which is every moment for a track that is not
repeating.

### The music is on a NAS

**Starting a track is a one-off cost and then almost nothing.** Measured here: a
cold first play was ~290ms from Enter to sound and a warm one ~55ms. Most of that
is Media Foundation resolving a source for the first time (~150ms) — loading the
byte-stream handler and the codec — and it is *per-process*, not per-file, which
is what makes `AudioPlayer.Warm` worth having. It is called from the tray via
`_ui.Post`, so it runs once the window is up: this application is the shell's
handler for every folder on the machine and is started afresh on every folder
open, so nothing that costs a tenth of a second belongs on the way in.

`Warm` opens a decoder, reads a little and throws it away. It never touches a
device, so unlike the media engine's preload — which loaded a source into the
thing that renders, and needed `AutoPlay` off and the volume set to zero to stay
silent — there is nothing here that could make a sound even if it went wrong.

**The music is on a NAS, and that is the dominant cost of everything.**
Local numbers are not the numbers that matter here. Measured over SMB against a
2170-track library:

| | local file | untouched NAS track |
| --- | --- | --- |
| Enter to sound | ~55ms | ~1400ms |
| a skip | instant | ~580ms, sometimes 1200ms |

It is the round trips, not the bytes — the *first 64KB* of an uncached file can
take a second, while a local file is free. Two different mechanisms deal with the
two different problems:

**Starting** is helped by `AudioPrefetch`, which pulls the head of whatever the
cursor has settled on through the operating system's cache. Nothing is kept; the
bytes go into a reusable buffer and are thrown away, and the copy that matters is
the one Windows now holds. Worth a median of 1433ms → 1166ms — about 19%, and
noisy enough that three interleaved trials of nine came out slower. Real but
modest: a NAS track will always take about a second to start.

**Skipping** is fixed by `StreamingSource`, which plays the track *through* a
download the way a video site does. Media Foundation reads the file through an
`IStream` instead of opening it; a worker fills memory from the beginning, and
each read is served from whichever copy answers fastest — memory for anything
downloaded, the share for anything not reached yet. A skip into downloaded audio
is **1ms**, from the first second of the track, with no swap, no reload and no
gap.

Two things were tried first and are worth not repeating. Pulling the file through
the *operating system's* cache only halved a seek (583ms → 316ms), because
Windows does not promise to keep forty megabytes of somebody else's file and did
not. Copying the whole track to a local file and switching the engine onto it did
give 0ms seeks — but only once the copy had finished, which for a 36MB track over
this link is five to eight seconds of skipping still being slow, and the switch
itself was a reload in the middle of the music.

Memory rather than a temporary file: RAM has no seek time, nothing to clean up
after a power cut, and the whole point is that a skip costs nothing. The cap
(`AudioPlayer.CacheBytes`, 1GB) is for the pathological case, not the ordinary
one. It was a preference, and so was whether to download at all; both are fixed
now, because turning the download off was never a taste — it was a way of making
skipping slow again.

Three rules that keep it honest:

- **One track at a time.** Starting another disposes the last, which frees its
  memory. Nothing accumulates.
- **Paused long enough and the memory goes back** (`AudioReleaseAfterMinutes`,
  10). `Release` frees the buffer and stops downloading; the track keeps working
  because reads simply go to the share again, and playing it resumes the
  download. Nothing is unloaded, so there is no state to restore.
- **A read beyond the download is served from the share, not waited for.** A skip
  into a part that has not arrived is network speed, which is what it would have
  been anyway — never a stall waiting for the downloader to walk there.

**Two things that sound like they would make starting faster, and do not.**

- **Reading the file with several parallel readers.** The link saturates at about
  7-8MB/s whether it is fetched by one reader or sixteen; the cache already fills
  as fast as the network allows. Measured across 1, 2, 4, 8 and 16 workers, in
  both orders.
- **Media Foundation's real-time mode** (`MF_MEDIA_ENGINE_REAL_TIME_MODE`). It
  genuinely halved the start — median 1642ms to 1091ms — and paid for it with
  about two dropouts per minute of network playback, up to nearly a second of
  silence each. Normal mode had none in the same test. It was a setting,
  `AudioLowLatencyStart`, default off, and it went with the media engine: the
  buffering it shortened is now the ring in `WasapiPlayback`, which is sized
  here rather than asked of Windows. The trade is the same wherever it is made
  and the measurement is the reason it is not taken — half a second sooner is
  not worth a gap in the middle of a song. Shrink `RingFrames` and you are
  buying the identical bargain; re-run the dropout measurement first.

**Put a watchdog in every audio harness.** These harnesses wait on a network, a
decoder and a clock that may never move, so a wrong assumption does not fail — it
sits. Several sat for twenty minutes before anyone learned anything. Every one of
them now starts a background thread that prints and calls `Environment.Exit`
after sixty seconds, every wait has a short ceiling of its own, and every step
prints as it happens rather than at the end, so a run that dies still says how
far it got. That is what turned an unexplained hang into the one-line crash above.

**Measure a network share with interleaved A/B and medians, never a single run.**
This share varies by more than the effect being measured: the same seek took
82ms and 2000ms on different tracks minutes apart. A single before/after
comparison first said the read-ahead made starting 2.3x faster and then said it
made it slower, and both were noise. Alternate the two conditions over a dozen
untouched files and compare medians, or measure nothing at all.

**Read-ahead is off for local disks, always.** There it buys nothing worth the
reading and competes with everything else that wants the disk, which is measured
rather than assumed — so `PrefetchAudioFile` passes `remoteOnly: true` and there
is no longer a switch saying otherwise.

**And the read itself is asynchronous, which is about threads rather than
speed.** It was a synchronous `Read` on a thread-pool thread: that holds the
thread for the whole two megabytes and cannot see the cancellation token until
the read it is inside of returns, so the token was cooperative in name only.
Arrowing through a folder of network tracks starts one of these per row rested
on — a row a second is a second's worth of blocked threads standing on each
other — and the pool only grows by about one thread every half second once it
runs out. Everything else the application does off the UI thread queues behind
that: the next folder's enumeration, the idle availability probe, a folder-size
walk. It is felt as the window going slow while arrowing, which is a long way
from anything to do with reading ahead.

**Arrowing through a folder must not open every file in it.** The read-ahead is
hung off a settle timer in `MainForm.SchedulePrefetch` — 350ms of the cursor not
moving — and skipped entirely while `_suppressStatus` is raised, because a bulk
selection is not somebody choosing a track. Two thousand rows would otherwise be
two thousand opens on a share.

### Holding a key down

**A hotkey can register cleanly and never fire, and nothing will tell you.**
`RegisterHotKey` reports a conflict only against another `RegisterHotKey`.
Anything with a low-level keyboard hook sits above it and takes the keystroke
first, leaving a registration that succeeded, reports no conflict, and does
nothing.

This is not hypothetical: the player first shipped with `Ctrl+Alt` and the four
arrow keys, which is **NVDA's table navigation**. All four registered, none
fired, while `Ctrl+Alt+P`, `+S`, `+M` and `+W` worked on the same keystroke
path in the same session — which is what the asymmetry proved. The defaults are
letters now (`U`, `D` for volume, `B`, `F` for seek), and `Settings.Migrate`
moves anyone still on the arrow defaults across.

Two consequences: never default an audio shortcut to `Ctrl+Alt`+arrow on a
machine that runs a screen reader, and **the tray menu carries play/pause, stop
and "what is playing"** — a player whose only route in is a shortcut that can be
silently eaten is a player that cannot be stopped.

**Every hotkey is registered with `MOD_NOREPEAT`, and repeating is done here.**
Windows will repeat a held hotkey by itself, but only at the keyboard's typing
rate — slow to start and slow to run, because it is set for typing. `HoldRepeat`
drives it instead, at `HoldRepeat.Interval` (20ms, fifty a second), which is what
makes holding the volume key sweep rather than crawl. The two together would
double every press, which is why the registration is uniform.

`AllowRepeat` in `AudioActions.All` says which actions repeat: volume, skipping,
speed and **play/pause** (that one is deliberate and was asked for — holding it
toggles as fast as the rate allows). Stop, mute, repeat and "what is playing"
fire once however long the key is held.

**A press is not a hold, and without a delay there is no such thing as a press.**
A finger is on a key for about a tenth of a second. At fifty repeats a second
that is five more of whatever the key does — so pressing play once played,
paused, played and paused again, landing wherever it happened to stop. Every
keyboard in the world waits before repeating; `HoldRepeat.Delay` is that wait,
and it is the difference between a working play button and one that "plays for a
second and then pauses".

It is **200ms**, not the 400 this first shipped with. Four tenths of a second
reads as the key ignoring you before it gives in — "holding the keys takes too
long to register" — and Windows' own fastest keyboard setting is 250. Two tenths
is still twice as long as a deliberate tap, which is the only thing the delay has
to beat.

**The delay, the rate and the acceleration are constants now, not preferences.**
All three were measured into place rather than chosen, and each had exactly one
right answer — the two paragraphs above are the argument for the delay, ten
milliseconds is the floor Windows will schedule a timer at, and acceleration is
what makes a hold quick rather than the rate (one percent of volume fifty times a
second still takes two seconds to cross the dial). A tap is exactly one step
whatever the acceleration does, so there was never anything for that switch to
protect. Three fewer controls on a page walked one control at a time by keyboard,
and three fewer values that can arrive from a hand-edited file needing clamping.

**How far a held key travels is not the same question as what a repeat costs,
and confusing the two broke skipping.** The throttle below was removed on the
grounds that a seek had become cheap — about a millisecond, out of memory —
which is true and is about *cost*. Distance is a separate matter: at the general
rate of fifty a second, a ten-second skip covers **five hundred seconds of track
per second held**. A three minute song is finished in under half a second of
holding, and then it stays finished, because every further press clamps to
`duration - 0.25` and lands in the same place. Reported as "holding or hitting
skip isn't perfect, hitting my shortcut twice keeps it at the same place" — the
second press was fine; the first had already run to the end.

The decoder was never the problem and a probe said so: five presses of +10s
against the real file landed at 10.023, 20.054, 29.993, 40.024 and 50.055, and
five presses with no read between them landed within 30ms of the target.

Skipping is back on `NudgeRepeatMilliseconds` — eight a second, eighty seconds
of track per second held. **This application's own Preferences page had been
claiming that rate the whole time the code was running at fifty**, which is the
other half of why a test now compares them: a number that lives in two places
and is checked in neither will drift.

**The seek throttle is gone, and it was the streaming buffer that removed it.**
Skipping once ran on a slower clock of its own, coalesced behind a trailing
timer, and applied one seek when the key came up. All of that was working around
the same cost: on a NAS a seek meant a network round trip, so fifty a second was
fifty round trips the decoder had to service, and holding the key locked the
player up. `StreamingSource` made a seek into downloaded audio cost about a
millisecond. Fifty of *those* a second is nothing, so skipping now repeats at the
general rate with everything else and applies every press immediately. Measured
against the NAS, twenty presses of a five-second skip: **221ms for the burst,
median 0ms and worst 6ms per press**, landing at 105.7s with playback unbroken —
and on a track nobody had touched, with 1MB of 22MB downloaded, skipping a
hundred seconds past the end of the download cost **217ms, worst case 1ms**, and
it kept playing. Do not put the throttle back on the strength of the old
measurements; they were taken against a decoder reading the share directly, which
is not what it does now.

Two things do remain, for two different reasons:

- **Skipping does not accelerate** (`AccelerationCap = 1`). Five seconds fifty
  times a second is already four minutes of track per second of holding.
  Acceleration exists to rescue a step of *one*; growing this one only overshoots
  the end sooner.
- **Speed keeps its own slower clock** (`AudioActions.NudgeRepeatMilliseconds`,
  120ms). Its ladder is eighteen rungs from a quarter to four times, and a hold
  at the general rate crosses all of them before the first is audible.

**Acceleration is what makes a held key fast, not the repeat rate.** Ten
milliseconds is the floor Windows schedules a timer at, and every action the
player has costs a fraction of a millisecond — so the rate was never the
bottleneck. One percent of volume fifty times a second still takes two seconds to
cross the dial, which is a countdown, not a volume control. `AudioActions.
Accelerated` grows the step every third repeat instead: a tap is exactly one
step, and a hold crosses the whole range in about half a second. The ceiling
comes from the action's own `AccelerationCap`, so `AudioActions.All` stays the
single place that says how an action behaves under a held key.

**Do not inherit a `[ComImport]` interface to extend it. Repeat the methods.**
The engine is gone but the rule is not, and it cost most of a day to learn.
`IMFMediaEngineEx` was declared as `: IMFMediaEngine` on the reasonable belief
that COM interop lays the base out first. It does not. The call landed on a
different method, which read its two arguments as something else entirely and
corrupted memory — and the process died with `Fatal error. Internal CLR error
(0x80131506)` inside `InterfaceMarshaler.ClearNative`, on the way *out* of the
call, a long way from the cause. Write the base methods out again in the derived
interface: verbose, and it cannot drift.

**Three rules that were the media engine's and are worth keeping in view**, since
anything asynchronous with a load step can reproduce all of them. Never call back
into an object from inside its own event callback — Media Foundation dispatched
the event on a worker, and re-entering there was accepted and silently ignored.
Never issue a play against a source that is still loading *and* a seek beside it:
the play starts from zero the moment it is ready and the seek behind it is
discarded, which is how "carry on where I left off" did nothing from the day it
was written. And when neither of those is obvious from the outside, trace the
events — printing the sequence turned three wrong theories into one line.

None of that applies to the direct path, which has no load step to race: the
decoder is open or it is not, and a seek is a field the pump reads.

**A repeat is paced by how long it takes, not by how often it was asked for.**
The repeated action runs on the thread that owns the player, and play or pause on
a network source can take a hundred milliseconds or more. Asking for one every
sixty leaves that thread nothing to paint with, and Windows calls the window not
responding — which is exactly what holding play/pause did the first time this
shipped. `HoldRepeat.NextInterval` schedules the next one from how long the last
one actually took, never using more than half the thread, capped so that a very
slow action is still a repeat. The timer is one-shot and rescheduled at the end,
so the gap is measured from when the action *finished*.

**A global hotkey never sees a key-up.** The release happens in whatever
application has the keyboard, so `HoldRepeat.StillHeld` asks about the keys
directly with `GetAsyncKeyState`, modifiers included — letting go of Control
while keeping the letter down has ended the shortcut, and carrying on would be a
bare letter repeating into somebody else's window. There is a thirty-second
safety stop for the release that never arrives at all.

### The gain effect, and why not to try it again

**The volume curve is perceptual.** Loudness is not heard on a straight line, so
the linear curve has a bottom end that is not actually quiet — halving the
amplitude is nothing like halving what you hear. Squaring it puts 20 percent at a
twenty-fifth of full volume rather than a fifth, and leaves full volume exactly
where it was. Linear shipped first only because it was what the player already
did; `Settings.Migrate` moves everyone across.

**The gain effect is deleted, and this is why it is not worth trying again.**
`GainEffect` was an MFT inserted into the media engine's audio path through
`InsertAudioEffect`, and it was the cheap route to volume above 100: no decoding,
no rendering, the engine keeps doing what it is good at. With it inserted the
player was **silent**. Measured by changing nothing but the switch and reading
the application's own audio-session peak meter while a full-scale sine played:

| gain effect | session peak | window says |
| --- | --- | --- |
| on | **0.0000** | Playing |
| off | **0.9998** | Playing |

Twice each. Not the multiply: at 100 percent the gain was exactly 1 and `Amplify`
returned without touching a sample, and it was still silent. The effect's own log
said where — samples arrived, `ProcessInput` was called over and over,
`GetOutputStreamInfo` was polled six hundred times, and **`ProcessOutput` was
never called once**. The transform took every sample and the pipeline never came
back for any of them.

Everything else about it was correct — the vtable, the media types, the
negotiation, four separate rounds of it — which is what made it worth keeping
behind a switch for a while. It is gone now because the direct path makes the
whole question moot: owning the samples means gain is a multiply in a loop we
wrote. Do not reintroduce an MFT to get louder.

**No test could have caught it, and that is the lesson.** Every audio test plays
a *generated silent* WAV — deliberately, so the suite does not need somebody's
music library — and a transform that outputs silence is indistinguishable from
one that works when the input is silence too. `SamplesSeen` proves samples went
*in*, which is what it was written to check, and says nothing about any coming
out. **A test for an audio effect needs audio in it.**

`Tests.WriteTone` is that, and `DirectOutputTests` uses it: two tenths of a
second of a sine at a fiftieth of full scale, played through a real device, with
the peak read back and checked against the gain. Quiet enough to run on somebody's
machine, loud enough that a path emitting silence reads zero and fails. Amplitude
is a parameter and the assertion is a *ratio* — the point is that what comes out
moves with the gain, which is precisely what a silent transform cannot fake.

**And check that a new test fails against the bug it was written for.** The
`MFShutdown` regression test passed against the broken code twice before it was
right: first because the engine was built after the teardown instead of before,
and then because the count went negative rather than to zero, so the spurious
shutdown never fired at all. A test written from a theory proves the theory only
if you watch it go red. Both revisions are still in the file; the second is the
one that matters.

### How to measure any of this

**How to measure this application's audio at all.** Media Foundation uses the
endpoint's **hardware offload** path on this machine, which bypasses the software
mixer completely. So:

- WASAPI **loopback** capture reads zeros while the player is at full scale. It
  is not broken — it captures the software mix, and this audio never enters it.
  WPF's `MediaPlayer` (also Media Foundation) *is* captured, which is what made
  the difference look like a bug in this application.
- The **device-level** meter reads zero for the same reason.
- The **per-session** meter (`IAudioMeterInformation` on the session, GUID
  `C02216F6-…`, not the capture client's) is the one instrument that sees it.
- The engine's own volume is applied somewhere further down still — the session
  peak stays at 0.9998 and the session volume stays at 100% whatever the player
  is set to — so **no software instrument here can measure loudness**. Verify the
  volume *control* by what the player reports (`Ctrl+Shift+P` says "volume N
  percent", and it tracked 30, 80, and six presses to 86 correctly) and leave
  loudness to ears.

All of that is about the *engine*. **The direct path can be measured exactly**,
which is the single biggest thing option 2 bought: the samples are ours, the
session is an ordinary shared-mode render stream, and the per-session meter reads
it precisely (see the table above). Measure the direct path; infer nothing about
it from the engine's behaviour, and nothing about the engine from a meter.

**Sweep every render endpoint, not the default one.** `GetDefaultAudioEndpoint`
with `eConsole` is one device out of several, and this machine renders on another
one: a sweep of the default alone reported "the application owns no audio
session" while the player was audibly playing and the meter on device 1 read
0.1960. `EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)` and scan them all.

**"No session" is not "no sound", and it cost most of an afternoon here.** The
engine's offloaded stream does not appear in session enumeration at all on this
hardware, so a perfectly healthy engine looks identical to a dead one through
that instrument. Three separate wrong conclusions came out of it — a broken
handover, a broken engine, and an unbalanced `MFShutdown` — and the thing that
settled it in one measurement was **the clock**: `Ctrl+Alt+1` says the elapsed
time, read it twice a few seconds apart, and a position that advances is a track
that is playing. Use the clock for the engine and the meter for the direct path.

**The audio preferences page says how the sound is actually leaving.** "Playing
through: this application, 48000 Hz, 2 channels, system effects bypassed" — the
rate, the channel count, and whether raw mode was *granted*, which is a request
the device may refuse and therefore not something a tick box can answer.

Two mistakes were made in that one line and both are worth not repeating. A
successful start used to overwrite the media engine's diagnostic with the
renderer's, so the page showed the same sentence twice under two labels and the
answer one of them existed for was gone. And when the engine went, the label
"Media engine: not started" stayed behind — naming a thing that no longer exists,
reporting a state that means nothing, directly above the line that answers the
question. A recorded failure is *appended* to the live state rather than
replacing it, because the state a failure leaves behind reads perfectly well as
an answer, and reporting only that is how a broken start gets described as a
working one.

### Getting above full scale, and proving it happened

**Ways to get above 100 percent — settled.** There is no way past 0 dBFS without
touching decoded samples or hardware gain, so there were exactly three: an MFT in
the engine's pipeline (built, silent, deleted — see above); decoding and
rendering ourselves; or raising the endpoint volume. The last stays rejected as
an automatic behaviour, and would have stayed rejected however hard the other two
had been: the endpoint volume is shared with every other application, and an app
volume key that quietly moves the system slider is both surprising and hard to
put back if the process dies holding it.

**The second is built, and what it delivers is measured.** Through the running
application, against a generated sine of amplitude 0.05, read off the
application's own session meter:

| the dial | measured peak | what that is |
| --- | --- | --- |
| 0 | 0.0000 | silence, reached by holding the key |
| 97 | 0.0470 | 0.05 x 0.97 squared, the perceptual curve |
| 392 | 0.1960 | exactly 3.92x |
| 1000 | 0.4999 | exactly 10x, the ceiling |

Holding volume-up crosses the whole range in about 2.5 seconds and holding
volume-down reaches exact silence. Raw mode is granted on this hardware, so the
system effects really are bypassed — the audio page says so in as many words.

**The decoder must never unbalance `MFShutdown`.** Everything here stands on the
same platform. `AudioDecoder` counts its `MFStartup` calls, and it used to
decrement that count in `Dispose` whatever had happened — so a decoder disposed
twice, or disposed having never been opened, gave back a startup it never held.
`MFShutdown` does not fail and reports nothing; it takes Media Foundation down
for the whole process, and anything still holding a track simply stops answering.
The count is per-instance now (`_started`) and `Dispose` is idempotent. The test
warms the player *first* and then tears decoders down around it, because that
ordering is the bug — warm it last and a fresh `MFStartup` puts the platform back
before anybody looks, and the test passes against the broken code.

**A COM object must be created on the thread that will use it.** The renderer
opens its device on the render thread and `WasapiPlayback` opens the decoder on
the pump thread, each with a `ManualResetEventSlim` handshake and
`SetApartmentState(MTA)`. Built on the caller's apartment and read from a
worker, the first `ReadSample` fails the cross-apartment QueryInterface with
`E_NOINTERFACE` — from inside a loop whose exceptions nobody sees.

### Which device, which speed, and what it costs to leave it idle

**Choosing the output device, and moving to it without stopping.** `Ctrl+Alt+Shift+O`,
or Audio → "Output device…", opens the same shape of dialog the speed keys do:
one drop-down list, applied as it is arrowed onto rather than on OK, so the
device can be chosen by ear. Escape puts back what was playing.

That only works because the move is cheap, and it is cheap by construction:

- **The ring and the decoder survive it.** Only the renderer is replaced. The
  audio already read stays read, so the gap is the time it takes to open a
  device — tens of milliseconds — and nothing is reloaded or re-decoded.
- **A different mix rate does not mean reopening the file.** The source reader
  will re-negotiate its output type on demand (`AudioDecoder.Reopen`), so moving
  from a 48kHz device to a 44.1kHz one changes what the same reader produces. No
  second open, no re-parse, no network read.
- **The new device is opened before the old one is stopped.** A device that will
  not open leaves the music exactly where it was and says so — going silent
  because a choice failed is the wrong answer.

Measured in the running application: arrowing through both endpoints while a
track played moved the session from device 1 to device 0 and back, each time
active at the same 0.0470 peak, and the clock ran 1:49 → 1:56 straight through
four switches. A reload would have reset it.

**A remembered device that is gone falls back to the default — and for a long
time it did not, because the check was one step too early.** Reported as "this
folder will not play", about eight perfectly good 32-bit float WAVs; the real
answer was that a saved USB endpoint had been unplugged and *every* track in the
application was failing.

`IMMDeviceEnumerator::GetDevice` **succeeds for an unplugged or disabled
endpoint.** It hands back a working `IMMDevice` for a thing that is not there,
and the refusal only arrives one step later when `Activate` answers
`AUDCLNT_E_DEVICE_INVALIDATED` (0x88890004). `Open` fell back to the default only
when `GetDevice` failed, so the fallback never ran and the open failed at the
next line. The comment above it described behaviour the method did not have,
which is the worst kind of comment to have written. `IMMDevice::GetState` against
`DEVICE_STATE_ACTIVE` is the check that makes it true, and the interface already
declared it.

**And the message must not put the file in the subject.** It said *"Could not
play <name>: no audio output"* — the cause was in the sentence, after a colon,
and it was read exactly as the first half said: as a fault in the file. A folder
got taken apart looking for what was wrong with its contents. This file already
warned about it two paragraphs before the code that did it ("hearing it blamed on
one file sends somebody to look at the file") and the warning did not survive
contact with the sentence. The file is not named at all now: a renderer that will
not open affects every track, so there is no file to name.

The test uses an id that has never existed, which exercises the same path as one
that has stopped existing and needs nobody to unplug anything.

**The empty id means "follow the system", and that is not the same as naming
today's default.** Storing the default device's own id pins the audio to the
speakers for ever; storing nothing lets plugging in headphones move the music.
An id belonging to something no longer plugged in falls back to the default
rather than refusing to play.

**Every speed works for every file now, and the apology is gone with the
restriction.** The dialog used to filter its list by what the decoder would
agree to, and that was a real filter: the rate belonged to the media engine,
Windows' FLAC decoder refused everything above normal, and a whole library of
FLAC showed a list where half the entries silently did nothing — with a grey
label explaining why. `TimeStretch` works on decoded samples and cannot tell
what container they came out of, so `SupportsSpeed`, the filter, the label and
the `audio.speed.refused` notification are all deleted. A test plays a real FLAC
at 50, 100, 200 and 400 percent and asserts the clock moves at each.

**Battery: stop the device when nothing is playing.** This is not a
micro-optimisation and it is worth stating plainly. An audio client that has
been Started keeps asking for buffers at the device period for as long as it
exists — about a hundred times a second, on a thread at Highest priority,
filling them with silence — and it holds the endpoint awake while it does. A
track paused and left alone did exactly that, indefinitely.
`WasapiRenderer.SetSuspended` stops the client on pause and starts it on resume.

The pump had the same shape of fault: it slept five milliseconds whenever the
ring was full, which is two hundred wakeups a second to discover there is
nothing to do — and paused, the ring never drains, so every one of them was
wasted. It waits on an event the render callback sets when it takes frames, with
a 250ms fallback.

Measured, paused, on this machine:

| | audio session | CPU |
| --- | --- | --- |
| before | **ACTIVE** | 8.9–10.4 ms/s |
| after | **inactive** | 2.6 ms/s |

The session state is the part that matters and the part that is not noise: the
endpoint is genuinely released, so the hardware can idle. Check it that way
rather than by the CPU figure, which moves around.

**The streaming buffer survived the engine, and had to be re-plumbed to do it.**
`StreamingSource` is an `IStream` that downloads a remote track in the background
and serves each read from whichever copy answers fastest — it is what makes a
skip on the NAS cost about a millisecond instead of half a second, and it fed the
engine through `IMFMediaEngineEx::SetSourceFromByteStream`. With the engine gone
it feeds `AudioDecoder` instead: `MFCreateMFByteStreamOnStream` wraps it and
`MFCreateSourceReaderFromByteStream` reads through it. Same object, same
argument, one interface lower down. Removing a backend is not a licence to drop
what was hanging off it.

### Loudness, headroom and the ceiling

**Gain does not clamp float, and the reason is measured.** Clipping at ±1.0 is
the application deciding how loud the hardware is allowed to be. Measured through
WASAPI loopback with the Windows volume at 10%, rendering a float sine straight
into shared mode:

| amplitude | peak at the endpoint |
| --- | --- |
| 0.5 | 0.063 |
| 1.0 | 0.126 |
| 4.0 | 0.504 |
| 8.0 | 0.986 |
| 16.0 | 0.986 |

Exactly proportional past full scale, then a ceiling. So the shared-mode mixer
carries samples above 1.0, scales them by the endpoint volume, and clips at the
*endpoint*. Where that falls depends on where the Windows volume is: at 10% there
were eight times full scale of headroom, and the old clamp threw all of it away.
At 100% there is none and the same clip happens one step later — which is the
point, because it is then the hardware saying no rather than this application
saying it on the hardware's behalf. `WasapiRenderer.ApplyGainAndMeasure` counts
what went past full scale rather than squashing it, so "is it clipping" is
answerable by somebody who cannot look at a meter.

**And the meter has to come out somewhere, or it is not a meter.** `ReadPeak` and
`ClippedSamples` were measured on every buffer from the first version of the
renderer, by a player whose stated reason for owning the render loop was to be
able to answer that question — and nothing anywhere ever asked either of them.
The count was also never reset, so what it held was "since this process started",
which for the shell's folder handler is a figure about a track nobody remembers
playing. It is reset when a track starts, `ClippedPercent` is the number worth
saying (a million clipped samples is either an overdriven track or four seconds
out of an hour), and `AudioPlayer.DescribeLevel` puts it on the audio page.

**The limiter is an upward compressor, and it does not stop clipping.** That is
the whole design, not a shortcoming of it. What it does is lift whatever is quiet
at that moment towards a ceiling of 0.9 — room tone, reverb tails, the breath
before a line, the second guitar in the far corner of a mix — so the things
living thirty and forty decibels under the music come up where they can be heard.
Loud material is never turned down: the gain it applies is clamped at one from
below, so above 100 percent the peaks clip exactly as they would have.

How far it goes grows with the *square* of the gain (`BaseBoost * drive * drive`,
eight at normal volume). That is the point of the shape: pushing the volume up
does not merely make the same sound louder, it opens the background up further,
and at the top of the dial the background is the track. It replaced a soft
clipper with a fixed knee — `Soften`, a hyperbola from 0.75 up — which did the
opposite thing, and quietly: it made a driven signal *quieter* than asked for,
which is the one outcome somebody turning the volume past full scale is not
asking for.

Three things in it that are not decoration:

- **One envelope for every channel, from the loudest of them.** Following each
  separately moves the stereo image around whenever one side is busier, which is
  the same mistake `TimeStretch` is careful not to make with its correlation.
- **Half a millisecond of attack against a quarter second of release.** That
  sounds far too fast for a compressor and is not, because of the release: what
  it follows is the peak envelope, which rises with the music and falls slowly.
  With a fast release as well it would track the shape of a bass note and
  modulate the gain at the note's own frequency, which is intermodulation
  distortion rather than a limiter. And there is no lookahead, so the attack is
  also the whole of the overshoot — at two milliseconds that is a click at the
  start of every phrase after a quiet moment, and at a half it is an edge.
- **The envelope starts where the music is.** Ramping it up from zero left the
  limiter wide open for the first couple of milliseconds of every track. Brief,
  and at a ceiling of a hundred times, brief and very loud.

The time constants are seconds and the loop counts frames, so `Coefficient` works
them out from the device rate. Getting that wrong is a limiter that behaves
differently on different hardware and sounds like a fault on one machine only.

**At or below full scale it does nothing, and that is the fix rather than the
design.** The ceiling was `BaseBoost * drive * drive` with `drive` clamped up to
one, so at a gain of one the limiter was still allowed *eight times* — eighteen
decibels of upward compression at 100 percent volume, and the same eighteen at
six percent, under a switch whose own description says the effect grows with the
volume. It does not grow from eight; it grows from nothing. `limit` is now
`Limiter && (LimiterWholeRange || gain > 1f)`, so without the whole-range switch
the limiter is exactly what it says it is: something that happens above 100.

That switch is the only way to have it below full scale, which is what it was
always for — listening *into* a recording rather than making one loud.

**The whole chain at rest is bit for bit the file.** Gain is exactly `1f` at 100
percent on both curves, `x * 1f` is exact for every float, the equaliser bypasses
when off or flat, and `TimeStretch` bypasses at rate 1. Each of those was tested
on its own and the promise they add up to was tested nowhere — which is the only
promise anybody has. `NeutralChainTests` asserts it end to end, comparing raw bit
patterns rather than values so that a stage quietly returning 0.9999997 fails.

**Its five controls are all on the Audio menu, and none of them is in
Preferences.** Attack, release and ceiling are three different
ways of describing something that can only be judged by ear against the actual
music, so a page you set them on and then go and listen makes you guess and then
check. `LimiterForm` is three lists on the same terms as `SpeedForm`: every arrow
press lands on the sound within one buffer, and Escape puts all three back.

Three consequences worth keeping:

- **The switch is in the dialog, and one effect has one home.** It used to be a
  tick box on the Audio preferences page while the four things that shape it
  were on the menu, joined by a rule: the menu entry appeared only while the box
  was ticked. So finding the limiter's controls meant knowing that a page you
  were not on decided whether a menu entry existed — worse than the problem that
  rule solved, which was "controls that do nothing". And the switch is the
  control you most want to flick back and forth *while listening*, which makes
  it the last one that belongs behind Ctrl+P, a category list and an OK button.
  The preferences page keeps the meter, which is a report rather than a control,
  and a sentence saying where the controls went.
- **The menu entry is always there, and ticked when it is running.** Both it and
  the equaliser's entry carry their own switch now, so neither ever leads
  somewhere that does nothing — and the tick says whether the effect is on
  without opening anything, which is what the preferences tick box was really
  for. Nothing is hidden and the menu never changes length.
- **Opening it does not switch anything on.** It used to, because the switch was
  somewhere else and three lists shaping a bypassed effect are three lists doing
  nothing. With the switch in the window there is nothing to work around, and a
  dialog that silently changes the sound on its way up is one you cannot use to
  find out what the sound was.
- **The entries are sentences, not numbers.** "Half a millisecond, an edge on
  transients" rather than "500". The whole dialog is meant to be used with your
  attention on what you are hearing, and a list reading "500" is no help at all
  to somebody deciding whether that is too fast.

The ladders live on `AudioPlayer` beside `SpeedLadder`, for the same reason: the
useful values are not evenly spaced. Attack matters below a millisecond and
hardly at all above ten, release is the other way round, and a slider would hand
out 0.37ms — which is not a decision anybody made. A test checks that each
default is *on* its ladder, because a dialog that opens on the nearest rung to a
value that is not one has silently moved a setting by being opened.

Nothing here renders 16-bit any more, which is why the old integer clamp is gone
with it. If one ever comes back it needs the clamp: an integer sample has nowhere
to put the excess, and wrapping takes the loudest possible sample to the
quietest, which is a click on every peak rather than distortion.

**Bypassing the system effects is a different mechanism, and it is now
reachable.** `AUDCLNT_STREAMOPTIONS_RAW` through
`IAudioClient2::SetClientProperties`, which the media engine did not expose at
all — that inaccessibility was one of the reasons for owning the render loop.
`WasapiRenderer` asks for it and `RawMode` says what was granted; on this
hardware it is granted, and the audio page says so in as many words. It is a
request, not a demand, and shared mode throughout: exclusive mode would take the
endpoint away from everything else on the machine, which is not a trade a file
manager gets to make on somebody's behalf.

**A ceiling that depends on something not yet created will be applied wrongly.**
`VolumeCeiling` used to have three states, because the dial could only exceed 100
if Windows had accepted the gain transform — and the state *before anything had
been created to ask* was the one that got it wrong. The saved settings are
applied in exactly that state: `TrayApplicationContext` calls
`ApplyAudioSettings` and only then `WarmAudio`. So a saved 150 was clamped to 100
on the way in and written back to disk as 100 on the way out. Set it to 150,
quit, start again, and it is 100 for ever — which is what "the volume will not go
above 100" turned out to mean.

`VolumeCeiling` is now just `LoudestPercent`, because owning the samples means
the answer is known before the question: above full scale is a multiply, and it
is available from the first sample of the first track. Verified end to end: 392
percent set, quit, started again, came back 392 and measured at exactly 3.92x.
The general lesson survives the specific fix — a limit conditioned on a
capability you have not probed yet is a limit that will be enforced at the one
moment it cannot be known.

**The volume is saved by whoever closes last.** It can be changed by a global
shortcut with no window ever open, and the window is what normally writes the
settings out, so `TrayApplicationContext.Quit` saves too.

### Gapless, and the three different silences that word means

"It should always be gapless, every file" is three separate silences with three
separate causes, and only the last is what the word usually means.

**The join at a repeat, which was the worst of them.** Repeat used to be honoured
in `AudioPlayer.OnDirectFinished` — and by the time that runs the ring has
already drained to nothing, because draining is *how the render thread knew* to
raise `Finished`. Answering it with a seek then emptied the ring again and
refilled it from cold. Every step of that is a delay with no audio coming out, so
a repeat was most of a second of silence between the last note and the first.

The pump turns the decoder round itself now, the moment a read comes up empty, and
writes the start of the track into the same ring in the same pass. Nothing is
cleared and nothing waits. `WasapiPlayback.Loop` owns it, because **the turn has
to happen where the frames are produced** — anything further out only finds out
after the ring has emptied, and by then the gap has been heard.

The stretcher and the resampler are deliberately *not* reset at the loop point.
What they hold is the tail of the last hop, and letting it cross into the first
hop of the new pass is the join being seamless. At normal speed and pitch they
hold nothing at all.

**The front of a track.** A device started against an empty ring asks for buffers
that are not there and gets silence — a click and a gap at the start of every
track, worst on the ones slowest to arrive. The renderer is opened *suspended*
now and `ReleaseWhenBuffered` starts it once half a second is decoded.

Two traps in that, both found rather than reasoned about. A file shorter than the
floor never fills it, so the release has to fire on `_sourceDone` as well — and
it has to be checked **every time round the pump loop**, not only after a read
that produced something, or a short file reaches `_sourceDone` on a path that
never calls it and the device stays suspended for ever. That shipped for one test
run and the real-audio test caught it as a tone measuring exactly zero: a track
playing in total silence, which is a far worse gap than the one being closed.
`Resume` also has to respect the gate, or it undoes the floor.

**The middle of one.** `RingFrames` was one second. A single request to Google
costs most of a second, so one slow chunk emptied the ring. It is four seconds
now. The old note here worried that a deep ring puts stale audio in front of a
seek; it does not, because a seek empties the ring deliberately. Four seconds of
stereo float is about 1.5MB against a 160MB executable.

### Skipping a track that has not finished downloading

Two complaints — "skipping on Drive isn't fast enough" and "holding the skip key
repeats segments of the track" — and **one cause between them**: the download
walked the file from byte zero, so a skip forward landed somewhere it had not
reached. Everything else follows from that.

**A moving download window was tried for the slow half, and reverted. Do not do
it again in that form.** The idea: the download walks from byte zero, so a skip
past what has arrived reads from the network; make the held region a *window*
that relocates to the skip point. It fixes exactly one case and ruins the rest.
A skip forward moved the window, a skip back then fell *behind* it, and the
download was torn down and restarted on each change of direction — throwing away
the bytes being played and sending the next reads to the network. Holding a skip
key changes direction constantly, so what should be a sweep became a series of
refetches, and the reported symptom was "seeking is not accurate, it replays the
part I am trying to skip".

A track is a few megabytes and lands whole within seconds; after that every skip
is free. The sequential prefix is the right shape for the case that actually
happens, against the one the window was for. A test asserts `Prioritise` is gone
*and* that the note explaining why is still there.

**One lesson did survive it, and it is worth more than the feature was.** A
download that has been stood down does not stop when told — it stops when it next
looks, which is after the four-megabyte read it is already inside, and until then
it goes on writing at offsets measured from *its* start. `Release` has always
handled that by dropping the buffer rather than waiting for the thread. Reusing
the array "to avoid an allocation in the middle of a keypress" let a superseded
thread write a whole chunk into the middle of the live buffer at the wrong
offset: wrong audio, and where the decoder could make nothing of it, a read of
zero bytes. **`StartFilling` always allocates.**

**The wrong half, and it is the worse one.** That slow read happens on the pump
thread. A held skip key fires eight times a second, so by the time a block comes
back two or three further skips have been asked for and the audio belongs to a
position nobody is at any more — and it was written to the ring regardless. The
ring is empty at that instant, because the seek that started the read emptied it,
and the renderer is starving; so those stale frames went straight out of the
speakers before the next seek could clear them. Held down, that is a burst of
eighty-millisecond fragments from every position the key passed through, which is
exactly what it sounds like.

One line in `PumpLoop` fixes it: if a further seek is already waiting when the
read returns, drop the block. **After the read, not before** — the whole point is
that the world changed during it.

**And an empty read is not the end of the track.** A decoder reading through a
download can come up empty without being finished — a skip lands past what has
arrived and Media Foundation hands back nothing rather than blocking. Treated as
the end, that raises `Finished`, and `Finished` with repeat on seeks to zero: one
momentary gap plays the song again from the beginning, which is a very loud way
to report a hiccup. `EmptyReadsBeforeEnd` is three, about forty milliseconds —
free at a genuine end, since a finished decoder returns nothing for ever — and a
skip resets the count.

**Two tests guard the reads themselves**, against a fixture where every byte says
which offset it belongs at: sixty jumps around a fully downloaded track, and
sixty more against a source read *immediately*, so the reads race the fill thread
rather than following it. Reads ahead of the download go to the share and reads
behind it come from the buffer; both have to be right, and a torn hand-off
between them is exactly what "repeats segments" sounds like.

### Pitch, and pitch without tempo

**Two stages, one division.** `TimeStretch` changes the length and leaves the
pitch alone; `Resampler` changes both together, which is what a tape machine
does. Put one behind the other and they span the same space in a different
basis:

```
  stretch by R, then resample by F
    speed = R * F
    pitch =     F
```

So for a wanted pitch P and speed S: resample by P, stretch by S/P. That
division is the entire feature, it lives in `WasapiPlayback.ApplyRates`, and
both numbers are set from that one method — a moment with the new pitch and the
old stretch is a moment at the wrong speed.

With "change the tempo with it" ticked the division is simply not done: stretch
by S alone and let the resample carry the tempo along, which is the turntable
sound people mean by "slowed" or "chipmunked".

**Order matters and it is not arbitrary.** The resampler goes *behind* the
stretcher. The stretcher's job is to correct the length of whatever is actually
going to be resampled; putting it second would have it changing the length again
after the correction had been worked out.

Four things in `Resampler` that are not decoration:

- **Ratio exactly 1 is a bypass**, bit for bit, like `TimeStretch` at rate 1 and
  the equaliser when flat. Normal pitch must not pay for the feature or be
  coloured by it, and a test asserts the samples come out identical.
- **Catmull-Rom, not linear.** Linear interpolation is a triangular filter: it
  rolls the top off audibly *and* folds what it does not remove back down as
  aliasing — the two things a pitch control gets blamed for. Four points and a
  cubic cost a handful of multiplies.
- **The anti-alias filter is one-sided, on purpose.** Pitching *up* reads the
  source faster, so everything above Nyquist after the shift folds back down as
  tones that were never in the recording; the input is band-limited to
  `0.45 * rate / ratio` first. Pitching *down* contracts the spectrum, nothing
  crosses Nyquist, and filtering would only remove treble entitled to be there.
- **The fractional read position is carried between calls.** Rounding it to a
  whole frame at each buffer boundary is a phase step, which is a click at the
  buffer rate — the same mistake `TimeStretch` avoids by carrying its remainder.

**The clock has to account for both stages.** `WasapiPlayback.Position` counts a
ring frame as `_rate` frames of source — because the division makes the two
cancel — and adds the resampler's held input frames (worth `_rate` each, since
its input is the stretcher's output) on top of the stretcher's own. When the
tempo is linked they do not cancel, and a ring frame is worth `rate * pitch`.

**The tests are the mirror of the stretcher's.** There the question was "did it
change the length without changing the note"; here it is "did it change the
note", then the harder half — "without changing the length". Both measured with
a Goertzel against real audio.

One trap, and it cost a debugging pass: `Read` always writes from index zero, so
a test loop that passes the same array each time keeps only the last chunk and
leaves the rest silent. That measures as no tone at *any* frequency and looks
exactly like a broken resampler. Append, and measure a window from a second in,
past the stretcher's first few hops.

`Ctrl+Alt+Shift+H`, or the Audio menu beside the speed. A drop-down rather than
the equaliser's sliders, and the reason is the one that decided it there: a
slider is right for a curve whose *shape* is the thing being read, and this is
one number whose useful values are named intervals. "3 semitones up" is an
answer; a slider reading "3" is a number. Ticked in the menu when the pitch is
anywhere but normal — there is no switch to report, because zero semitones is
already a bypass.

### The equaliser

**Twenty bands from 32Hz to 20kHz, and it is behind the limiter.** `Equaliser`
is a bank of biquads run from `WasapiRenderer.ApplyGainAndMeasure`, and the one
thing to know about it before anything else is where it sits: *after* the gain
and *after* the limiter. That is not convenience. The limiter is an upward
compressor watching an envelope, so anything raised in front of it is something
it then reacts to — a band boosted six decibels arrives as rather less than six
and moves about while it does. Behind it there is nothing left to argue with the
setting, which is what "no compression on the bands" means.

It is also therefore the last thing before the meter, so `ClippedPercent` counts
what a boost actually did rather than what the signal looked like before it.

**The sliders go to ±24dB.** Twelve was the first number and it is the cautious
one — it is what hardware graphic equalisers offer, partly because an analogue
filter bank gets noisy past it, and none of that applies to arithmetic. A band
that cannot be pushed far enough to hear what it does is a band whose sound you
cannot learn. One decibel an arrow press, six a page, Home and End for the
extremes.

It is a great deal of gain and it is not defended against: twenty bands at +24
is sixty decibels into material that is already clipping. `ClippedPercent`
counts it and the audio page says so, which is the same bargain the volume dial
makes at 1000 percent — this application reports what went past full scale
rather than deciding on the hardware's behalf that it may not.

**The ladder is ISO preferred numbers at half an octave**: 32, 45, 63, 90, 125,
180, 250, 355, 500, 710, 1k, 1.4k, 2k, 2.8k, 4k, 5.6k, 8k, 11.2k, 16k, 20k. An
octave apart is coarse enough that pulling one band takes an instrument with it;
a third of an octave is finer than one slider can be heard and is thirty-one of
them to arrow through. The last step is narrower than the rest because 22.4kHz —
where the ladder would otherwise go — is above the Nyquist frequency of a CD.

Four things in it that are not decoration:

- **Q of 2, and the sweep behind it.** Filters each exactly as wide as the gap
  to their neighbour leave a dip between every pair. Wider ones overlap and the
  dips fill in — but the same overlap means every band is being lifted by its
  neighbours too, so the total runs away. With all twenty at +24, measured at a
  band centre and at the gap beside it: Q 1.2 gives 84.7dB with 2.8 of ripple,
  Q 1.4 gives 76.3 with 3.6, Q 1.6 gives 69.8 with 4.3, Q 1.8 gives 64.4 with
  5.0, Q 2.0 gives 60.1 with 5.7.

  **The ripple is about a tenth of the lift at every one of them.** That is the
  real shape of it: not a defect a Q setting fixes, but what a bank of fixed
  filters does, and the only question is where to sit on the trade. Two, because
  what has to be exact is *one slider* — a single band at +24 measures +24 and
  its neighbours three octaves away do not move. All twenty at maximum is 60dB
  into a signal that is already clipping, which is a setting with no musical
  meaning and the wrong one to design the useful case around.

  The test asserts the ripple as a *fraction* of the lift rather than a number
  of decibels, at +6, +12 and +24. An absolute threshold would pass trivially at
  one end and fail inevitably at the other while describing the same bank.
- **The two ends are shelves, and their corners are between the bands.** A
  peaking filter at 32Hz leaves everything below 32Hz alone, so pulling that
  slider down to cut rumble would cut a band and leave the rumble. The corner
  placement is the part that was wrong first and is worth remembering: a shelf
  is at *half* its gain at the corner, so a high shelf cornered on 20kHz reads
  **+2dB at 18kHz** for a slider set to +12, with everything else it could have
  lifted above a CD's Nyquist frequency. The top slider did almost nothing. Each
  corner is the geometric midpoint between the end band and its neighbour now,
  so each shelf takes over exactly where the last peaking band's reach ends and
  the number printed under the slider sits in the part with the whole gain.
- **The state is `double`, and transposed direct form II.** A 32Hz biquad at
  48kHz has poles very close to the unit circle; in `float` it spends its
  precision on them. The transposed form holds state on the order of the signal
  rather than the signal divided by the pole radius, which is the difference
  between a clean bottom end and a noisy one.
- **Gains glide over twenty milliseconds.** Swapping coefficients between one
  sample and the next steps the output, and a step is a click — on twenty bands
  at once, a crunch.

**Off is a bypass, and flat is too.** Not a bank of filters set to unity: a
biquad at unity gain still rounds every sample. `TimeStretch` makes the same
promise at rate 1 for the same reason, and it is also what makes a curve
comparable against *no* curve rather than against a slightly rounded one. The
suite asserts both paths are identical bit for bit.

**And the tests have audio in them.** This is the file where that rule was
learned — see "the gain effect, and why not to try it again" above — so every
check feeds a real sine through the real `Process` and reads a real frequency
back. The one that matters most is not that a raised band gets louder, which a
volume control would pass, but that **125Hz and 8kHz do not move when 1kHz is
raised twelve**. That is the whole difference between an equaliser and a gain,
and it is the only assertion a silent-or-broken transform cannot fake.

`Ctrl+Alt+Shift+E`, or the Audio menu. Sliders rather than the drop-down lists
`LimiterForm` and `SpeedForm` argue for, and the distinction is real: a list is
right when the useful values are unevenly spaced and few, and decibels are
neither — they are already a perceptual scale, every value on it is worth
having, and twenty of them make a *curve* whose shape is the thing you are
reading. Unlike the limiter's entry the menu item is always visible, because
opening the dialog switches the equaliser on, so it never leads anywhere that
does nothing.

### The ring is latency, and depth is a choice

**Everything in the ring has already happened.** It holds audio that has been
decoded, stretched, resampled and had its silences taken out, so a change to any
of those stages is not heard until what is already in front of it has played.
Filled to the brim that is four seconds — which is what "adjusting the pitch is
not instant" was, and it was not a bug in the pitch control. The volume, the
limiter and the equaliser have always been instant for the opposite reason: they
run on the render thread, *behind* the ring.

The four seconds exists for exactly one case, and it is written down where
`RingFrames` is: a track coming off Google Drive through a download that is itself
racing playback, where one request to Google costs most of a second. That case is
the one `StreamingSource.Complete` answers. Once the file is in memory — and for
a local file, from the first frame — there is nothing left to absorb.

So the ring is four seconds *wide* and kept at `ShallowFrames`, a fifth of a
second, unless a download is still arriving — which is asked as
`Available < Length`, **not** `StreamingSource.Complete`. Complete means the
cache budget is full and the budget is capped at a gigabyte, so a track bigger
than it reports complete with most of itself still on the network. That is not
hypothetical: the folder that turned up the Ogg bug below holds a **1.3GB** Ogg. A fifth of a second is ten renderer
buffers of head start against a decode that runs hundreds of times faster than
playback, and it is short enough to read as immediate on a control being set by
ear. The capacity does not change with it, so a stream that has not finished
arriving still gets the whole of it, and the ring simply drains to the shallow
level once it has.

Two things that had to move with it:

- **The prebuffer floor is `Math.Min(PrebufferFrames, TargetFrames())`.** A floor
  above the level the pump has decided to fill to is a device held suspended
  for ever, waiting for audio nobody is going to decode. The suite reads the
  source for that line, having failed on it once.
- **Everything remote already goes through `StreamingSource`.**
  `AudioPrefetch.LooksRemote` covers UNC paths, network drives and cloud
  placeholders, and "download the track into memory" has no switch any more — so
  "not streaming" means a genuinely local file, which is the one that never
  needed the depth.

### Silence reduction

**It is a pull stage, not an effect, and that decides where it lives.** The gain,
the limiter and the equaliser are arithmetic on a buffer that goes into
`ApplyGainAndMeasure` and comes out the same length. This one *removes* audio, so
there is nowhere in the renderer to put it: it is a `Source`/`Read` stage like
`TimeStretch` and `Resampler`, and it sits at the very front of the chain —

```
decoder → SilenceReduction → TimeStretch → Resampler → ring → renderer
```

— **in front of the stretcher, at the decoder's own rate**, so "gaps longer than
four hundred milliseconds" is four hundred milliseconds of the recording. Behind
the stretcher the same setting would mean a different length of music at every
speed, which is a control nobody can learn.

**Five numbers, one switch, and every one of them lands on the sound as it is
moved.** `Ctrl+Alt+Shift+Q` by default and rebindable like every other audio
shortcut — the page is built from `AudioActions.All`, so an action added to that
list gets a box on it without the page being edited. Or the Audio menu. The
dialog carries its own switch, like the limiter's and the equaliser's, so the
menu entry always leads somewhere that does something.

| | |
| --- | --- |
| **Silence is below** | −80 to −1 dB. What counts as a gap |
| **Only gaps longer than** | 1ms to 5s. Anything shorter is played exactly as recorded |
| **Shorten them to** | 0 to 2s. What is left of a gap that was cut |
| **Fade out over** | 0 to 500ms. The music going away |
| **Fade back in over** | 0 to 500ms. The music coming back |

There was a sixth, "silence what it leaves", which replaced the surviving gap
with zeroes. It is gone, and so is the code behind it — a switch removed while
what it switched stays behind is dead weight the compiler cannot see. Its one
real consequence went with it: the fade out used to end wherever that box said
the sound stopped, and now it always ends at the cut.

**These are spin boxes, and they are the only ones in the application.** Every
other number in it is a drop-down list, for the reason `LimiterForm` sets out: a
list cannot hold a value nobody offered, and it reads its value out on every
press. That is the right trade for an attack time, whose useful values are
decades apart. It is the wrong one here — these are lengths of silence in
milliseconds, every one of them is a value somebody can want and can hear, and a
ladder of suggestions is a ladder that decides for you.

**Two ways of keeping the list were tried and both failed, so neither is to be
tried again:**

- **An editable combo box** — the ladder for arrowing, typing for anything else.
  It stopped announcing what it was set to. A control that cannot be heard is not
  a control in this application, and no amount of typing being possible makes up
  for it.
- **A list holding every value in its range.** It announces perfectly and it takes
  **1030ms to open.** Eight thousand items across five combo boxes, and a combo
  whose handle exists takes one window message per item: 105ms building the
  strings, nine hundred more handing them to Windows. Measured, not guessed, and
  the measurement is still in the suite — the check now times the dialog through
  `Show` rather than stopping at the constructor, because the constructor was the
  cheap tenth of it.

What a spin box gives up is the words, and the words are the point — "150" is not
an answer to "how long a gap" and "150 milliseconds, between words" is. So the
application says them itself, four hundred milliseconds after the arrowing stops.
Sweeping is a run of bare numbers, which is what makes sweeping quick; stopping
tells you what you have stopped on. It is the same bargain as reading the first
row of a folder aloud — the reader may say the number too, and hearing it twice
beats not hearing what it means.

Up and down move by one, Page Up and Page Down jump between the values the old
ladder suggested, Home and End are the ends, and the number can be typed. A spin
box brings none of those but the first, so `ProcessCmdKey` adds them: a range five
thousand long with a step of one needs a coarse gear.

**The far end of every one of those is deliberately absurd.** Minus one decibel
is very nearly the whole recording, one millisecond is shorter than a cycle of
anything below a kilohertz, and together they chop a recording into the handful
of samples that touch full scale. Nought decibels is the one bound here that is a
real limit rather than a taste: at nought nothing is above the threshold, the
whole recording is a gap, and the stage correctly hands back nothing at all — a
setting whose only outcome is silence is not a setting, it is a way to make the
player look broken. None of that is a range that escaped notice, and the argument
is the equaliser's ±24dB argument:
a control that cannot be pushed far enough to hear what it does is a control
whose sound you cannot learn. The honest consequence is checked by a test: a
threshold above the whole recording leaves nothing of it, the stage says so by
running out, and the track ends. Not a hang and not a crash.

There was a button that went to the far end of all four at once, and it is gone.
It was a preset for a setting nobody keeps, on a dialog whose every other control
is a thing you arrive at by ear — and one more item to hear past on the way to
the ones that are.

**The two fades are separate numbers, and the fade out ends at the cut** — across
the tail of the kept gap, and back into the music behind it when the fade is
longer than that gap. So it is never limited by how much of the gap is left,
which is the whole point of it being its own number.

Reaching backwards into music that has already been decided about is what the
delay line is for: a ring holding the most recent *fade out* of output, so the
music behind a gap has not gone out of reach by the time the gap turns out to be
worth cutting. At a fade out of nothing it is empty and every frame goes straight
through.

**The first version of this was one number, and it was wrong in a way worth
recording.** It took the fade out of the *front* of the gap it was leaving, which
meant "shorten them to nothing" silently also meant "and splice it" — the fade
control was quietly overruled by the control above it, with nothing saying so. A
control that another control can overrule is not a control. It also fell out of
the same mistake that the fade was being applied to the wrong material: the gap
is below the threshold by definition, so fading *it* out is fading out something
already inaudible. What anybody means by a fade out here is the music.

Both ramps are raised cosines rather than straight lines, for the reason
`TimeStretch` uses one — linear is the right gain law and its corners are not —
and the fade in is the same curve read backwards, so equal settings are
symmetrical.

**Detection is a peak over at most 128 frames; the fades are per sample.** Under
three milliseconds at 48kHz, which is short enough that a cut lands where the ear
says the gap started and long enough to hold a peak worth reading. Shorter blocks
are not more accurate, they are noisier: a 50Hz waveform spends whole
milliseconds near zero on its way past, and a detector looking at half a
millisecond calls the middle of a bass note silence.

**But the block follows the minimum down** — a quarter of it, floored at eight
frames. The minimum goes to a single millisecond, and a decision taken in 2.7ms
blocks cannot tell one millisecond from three: the control would go on offering
numbers it had stopped being able to tell apart, which is a dial with nothing on
the end of it. From 20ms upwards a quarter is already past the cap, so every
setting anybody listens at decides in the same full block it always did. It is
noisy at the bottom on purpose: asking for gaps of a millisecond is asking for a
detector that reacts inside one cycle of a bass note, and what it finds down
there is not gaps but the part of every waveform that passes near zero.

**Off is a bypass, bit for bit** — the same promise `TimeStretch` makes at rate 1
and `Equaliser` makes when it is off, and the suite asserts it sample by sample.
Switching it off part way through a gap hands back what was being held rather
than dropping it, so unticking the box cannot put a hole in the music.

**Two things the position display has to know about**, and only one of them is
fixed:

- What is held and not yet handed on is reported through `HeldSourceFrames` and
  subtracted, exactly as the two stages behind it report theirs. A gap that turns
  out to be too short comes out *all at once*, so a caller asking for less leaves
  the rest staged here; without this the clock runs ahead by however much is
  waiting.
- What has been **dropped** is deliberately counted nowhere, and the clock
  therefore runs ahead of the music across a cut by the length of that cut, until
  the ring drains past it. It is bounded — the error is only the removals inside
  the four seconds the ring holds, and it does not accumulate over a track — and
  fixing it properly means a per-frame ledger of how many source frames each ring
  frame stands for, which is a great deal of machinery for a clock that is a
  second or two optimistic while a gap is being squeezed. Known, measured, and
  left.

**The equaliser gave up its access key for this.** The Audio menu had taken P,
S, U, D, M, B, F, K, W, T, R, Y, I, O, A, C, V, L, E, H, G and N between its
twenty-two entries, and every letter in the words "silence reduction" was one of
them — so the entry shipped once as "Silence s&queeze", which was a feature named
after the only free letter rather than after what it does. J, Q, X and Z were the
free ones, and `E&qualiser` is the only label in the whole menu containing any of
them. So it took the Q out of its own middle and silence reduction has the E.

The *shortcut* did not move: `Ctrl+Alt+Shift+E` is in somebody's settings.json
and in somebody's fingers, and an access key inside a menu that is already open
is a far cheaper thing to change than a key that works everywhere in Windows.
They disagree now, and that is the cheaper of the two disagreements. A shared
access key stops acting and starts cycling, which a test checks — that test is
what found this in the first place.

**And the tests have audio in them**, which matters more here than for any other
effect in this file: a stage whose whole job is to remove audio has "removed all
of it" as its failure mode, and a test that only checked the output got shorter
would call that a success. So every check is against a real 440Hz tone with a
real gap in it, and the ones that matter are that the tone is still there at its
own amplitude on both sides of the cut, and that the gap is exactly as long as it
was asked to be — asserted to the frame, because the detection block divides both
and a tolerance would only hide a stage that was one block out.

### What a song says about itself

**Tags come from the Windows property system, not from a tag parser.** Windows
already reads ID3, Vorbis comments and MP4 atoms, and asking it means the numbers
in the song properties sheet are the numbers Explorer shows for the same file.
`AudioTags` opens an `IPropertyStore` with `GPS_BESTEFFORT`, so a file type with
no handler gives an empty store rather than an error.

Two traps, both of which shipped once:

- **An absent property does not fail, it converts.** A missing string comes back
  as `""` and a missing number as `0` — so "no tags" and "blank tags" arrive
  looking identical. A file with nothing set listed `Year: 0` and `Track: 0`, and
  a file with no title announced itself as `" — "`. Strings are treated as absent
  when blank; numbers are checked against `VT_EMPTY` before conversion.
- **Reading tags opens the file**, which can be on a share whose server has gone
  away. `SongPropertiesForm` reads on a worker and fills the list when it lands,
  and `AudioAnnounceUsesTitleTag` reads in the background and renames the track
  once the tag turns up — the file name is used until then, and stays if none
  does.

**A tagged file for the tests is written, not found.** `WriteSilentFlac` takes an
optional set of Vorbis comments and emits a real `VORBIS_COMMENT` block (whose
lengths are little-endian, unlike every other length in FLAC). Its frame numbers
are UTF-8 coded, so past frame 127 they stop fitting in a byte — which only
showed up once a fixture long enough to seek around in was needed. A test that needs
somebody's music library is a test that passes on one machine. Note that Windows
does **not** surface a WAV's `LIST`/`INFO` chunk as tags — only FLAC was worth
building the fixture for.

### Asking for a shortcut

**Preferences asks for a shortcut by capturing the keys, not with four tick
boxes.** Eight actions the old way would be forty controls in a dialog that is
walked one at a time by keyboard. `ShortcutBox` never captures Tab, Shift+Tab,
Enter or Escape, so the box can always be left and the dialog can always be
answered from inside it; Delete clears. It speaks what it captured, because a
read-only edit field gives a screen reader no reliable moment to read the new
value back.

### What the MFT attempt taught, now that the MFT is gone

Kept short and kept here, because every one of these is about Media Foundation
rather than about the transform, and the decoder still talks to it.

- **A managed array in a COM signature marshals as a SAFEARRAY.** This cost most
  of a day. `ProcessOutput` takes `MFT_OUTPUT_DATA_BUFFER*`; declaring the
  parameter as `MFT_OUTPUT_DATA_BUFFER[]` makes the CLR read a plain contiguous
  block as a SAFEARRAY and throw `SafeArrayRankMismatchException` **in the
  marshalling stub, before the method body runs** — so tracing inside the method
  shows nothing, because it is never entered. Take the pointer as `IntPtr` and
  use `Marshal.PtrToStructure`.
- **Verify a vtable slot before handing Windows a pointer to your own object.**
  Reaching a method deep in an interface means declaring everything before it
  correctly, and the call that passes *us* outward is the worst place to be
  wrong. Find a cheap method in the same region that returns something checkable
  and call it first.
- **An accepted object can still be silent.** `S_OK` from an insertion means it
  was stored, nothing more. Four separate mistakes each completed more of the
  negotiation and still produced no audio: `E_NOTIMPL` from an available-type
  method (documented as acceptable, and the resolver abandons the topology on
  it); refusing an unfamiliar media type; answering "set the other one first"
  from both directions, which is a deadlock; and
  `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES`, the tidiest thing an in-place effect can
  do, which the engine will not build a pipeline around.
- **And it was still silent with all four fixed**, which is the actual moral: a
  negotiation that completes proves the contract was satisfied, not that anything
  came out the other end. Only counting output proves that.

## What is spoken

**Every message the application can produce is in one list.** They were
scattered: a hundred and sixty-seven strings across a dozen files, each deciding
for itself whether to speak, whether to write to the status bar, and whether
anybody could turn it off. Three could be silenced, the rest could not, and there
was no way to find out what the application was even capable of saying without
reading all of it. `Notifications.All` is the catalogue; `SpeechForm` — reached
from the Speech page in Preferences — is the way to configure it.

**There is one question per message, and it is whether to speak it.** This was
three channels and eight combinations of them: status bar, speech, and a tray
balloon. The balloon is gone entirely. A Windows notification is a thing you have
to be looking at a screen to know happened, and this application is driven by
ear — so every one of them was either invisible or a duplicate of something
already said, and the icon-picking heuristic underneath them
(`LooksLikeAProblem`, twelve words looked for in an id) was deciding something
nobody was ever going to see.

The status bar stopped being a choice at the same time, because it never should
have been one. Writing a silent line into a bar costs nothing and interrupts
nobody, so it happens for every message the application delivers — and a message
routed nowhere used to leave the bar showing whatever it said before, which is a
stale answer to a question somebody has just asked. `SayAudio` had exactly that
bug against its own documented intent: "volume down" is off by default, so
holding the volume key moved the level and left the status bar reading something
else entirely.

So `NotificationChannel` has two members, `Choices` has two entries, and the
five names the catalogue is written in (`Off`, `Bar`, `Say`, `Pop`, `Loud`)
collapse onto them. `Pop` — the balloon-only ones, the drive being ready, an
install having worked, folders now opening here — became speech rather than
silence, because for somebody listening that was the only honest replacement.

**Each one has an example that renders.** That is what makes them testable and
previewable — a message can be checked, and heard, without waiting for the thing
to happen, and half of these only happen when something has already gone wrong.
A `Preview` button plays whatever the setting says *including when it is off*,
because a preview that refuses to demonstrate the thing being configured is a
broken button.

**Only differences from the default are stored.** `SpeechOverrides` is `id=1` or
`id=0` separated by semicolons. Writing all 171 out in full would be a settings
file nobody could read and a migration every time one was added, and an id that
no longer exists is dropped on load so a removed message leaves nothing behind.

It is deliberately a *different property name* from the `NotificationOverrides`
it replaces, and that is the migration. The numbers in it used to be a three-bit
set of channels and are now one bit, so under the old name a saved `1` would mean
"status bar, silent" to the file and "speak" to the reader — the one case a
migration cannot get right by guessing. A new name means an old file's value is
simply not found and every message goes back to its default, which for the vast
majority is the value it had anyway.

**The defaults follow the rule the audio player already lived by: say less, not
more.** Anything you can already tell by other means is off — three things
already speak when you move and a fourth is an echo. Anything asked for directly,
and anything that went wrong, is on. A test enforces the second half: nothing
whose id contains *fail*, *denied* or *refused* may be silent by default.

**The tests are mechanical because the failures are.** Two entries sharing an id
so one silently configures the other; a template whose placeholders do not match
its example, which throws `FormatException` at the exact moment the message is
finally needed; a rendered string still showing its own braces. None of that is
visible reading the list, and all of it is one loop.

**Building the window is itself a test.** 171 rows is exactly the sort of thing
that compiles, opens, and is quietly missing a category — the count is the only
thing that says so — so the suite constructs `SpeechForm` on an STA thread and
counts the choosers. It also times it: a dialog that takes five seconds to appear
is a defect whatever else it gets right.

**151 of the 168 are raised; the other 17 are marked, not pretended.** Wiring them
was done by four agents partitioned by *file ownership* rather than by category —
file operations, navigation, clipboard and searching all live in `MainForm.cs`,
and four agents editing one file is how you corrupt it. Each built to its own
`obj\agentX\`. None of them touched `Notifications.cs`: the `Raised` flags are
regenerated afterwards from the same source scan the test uses, so the flag and
the test cannot disagree with each other.

**The instruction that mattered was "never invent an event that does not
happen."** Twenty-one came back refused with reasons, and the reasons are the
useful output — a to-do list with the blocking reason attached, which is worth
more than twenty-one fabricated detection points. Four of them have since been
built, and each one was a missing return value rather than a missing event:

- `conflict.overwrite`/`rename`/`skip` were decided inside `RoboCopyEngine`
  and `FileOperations` and thrown away. Both now carry a `ConflictOutcomes`
  back, so a paste that renamed or replaced something says which file.
- `nav.offline` was impossible while `DirectoryExistsAsync` collapsed "timed
  out" and "returned false" into one bool. `ProbeDirectoryAsync` answers with
  three states and the old method is one line on top of it.
- `move.cancelled` existed only as `move.failed`, which meant silencing a
  deliberate Escape silenced genuine failures with it.

`hotkey.silent` remains undetectable, exactly as this file already said.

**A finer id replaces the action's generic one, never adds to it.** The player
names an event more precisely on the way past — `volume.max` rather than
`volume.up` — and announcing both would say the same thing twice under two
ids. `TrayApplicationContext` compares the *thread*, not a flag, to tell a
notification raised synchronously by the command it just called from one arriving
on a Media Foundation worker; a flag alone would capture a worker's event whenever
a key press happened to be in flight.

**The catalogue must be connected to something, and a test says how much.** It
was inert for a while: 167 configurable messages, a settings page, and no code
anywhere that consulted the setting before speaking. Every other test in the
section passed while it did nothing at all, which is the kind of green suite that
teaches you not to trust suites. `WiredNotificationIds` reads `src` and counts
the ids that really appear in it, and `NotificationInfo.Raised` marks the ones
that do. The test compares the two **both ways** — a flag set on something nobody
raises is the original lie in smaller type, and one missing from something that
is raised tells you a live message is dead. Preferences shows the count and marks
the rest "not raised yet".

**Routing comes from the catalogue; wording comes from the code.** The player
knows the real volume and the real time, so the sentence is still built where the
values are — `AudioActionInfo.NotificationId` and `SayAudio(id, message)`
only decide whether anyone hears it. The old per-category switches still apply on
top: they silence a whole group, and the catalogue silences one message inside a
group that is otherwise on.

**An id the catalogue does not know still gets through, to the status bar, and is
not spoken.** A message that exists in the code and not in the catalogue is a gap
in the catalogue, and swallowing it would hide the very thing that says so — but
a gap should be visible without being able to interrupt anybody.

## Google Drive

**Enumerating placeholders costs about fifteen milliseconds each, for ever.**
This is the single largest performance fact about the drive and it was not
written down anywhere. Measured on `music\artists`, 2118 tracks:

| | |
| --- | --- |
| `dir /b` on the Drive folder | **33 seconds**, and the same on the second and third run |
| `C:\Windows\System32`, 4527 entries | 310ms |

It never caches. There is no warm run. A hundred times the per-entry cost of
ordinary NTFS, paid on every single visit — and it is the whole of "why does
loading Drive folders lag".

The fix is not to make the walk faster, it is **not to walk**. `Populate` already
fetches every name, size and timestamp from Drive to create those placeholders,
and then threw the list away — so the only route back to facts the application
had just held in its hand was to ask the filesystem for them one placeholder at
a time. `DriveMount` keeps that listing now (`_listings`), `GoogleDrive.TryListing`
hands it out, and `MainForm.EnumerateSorted` asks before it considers a
`DirectoryInfo`. A Drive folder costs one Drive listing on the first visit of a
session and nothing at all afterwards.

Three things that keeps honest:

- **The names are the names on disk**, after `Sanitise` and `Unique`, not the
  names Drive returned. Drive allows two files in one folder to share a name and
  allows characters NTFS will not take, so a listing that handed back Drive's
  version would produce paths that do not open. That is why `DrivePlaced` is a
  different type from `DriveEntry`.
- **Refresh has to forget.** The pane is built from the remembered listing, so
  F5 would otherwise rebuild identical rows from the same stale answer and look
  like a refresh that does nothing. `RefreshAsync` calls `Drive.Forget` first.
  Re-walking the placeholders is not the alternative: that reads a picture which
  is exactly as stale, thirty seconds more slowly.
- **A delete has to leave the parent's listing.** Dropping the maps for the item
  itself is not enough — the folder above still has it in the list it was
  populated with, so a trashed file would go on being shown.

**A cloud provider of our own, not a mount from somebody else.** Drive appears on
a drive letter as ordinary files, built on the Windows **Cloud Files API**
(`cldapi.dll`) — usermode, in the box, no kernel driver to write or ship. rclone
was the obvious alternative and it needs WinFsp, which is a kernel driver with
newer and much less travelled ARM64 support; the Google desktop app is a
background process that wants to own the account. This needs neither.

The point is not that Explorer can see it. The point is that **our own code does
not have to know Drive exists**: the placeholders are real files with real paths,
so `EnumerateFileSystemInfos`, `RoboCopyEngine`, `ShellContextMenu`, `AudioTags`'
`IPropertyStore`, `FileLauncher` and the audio decoder all keep working untouched.
A virtual listing inside the app would have needed a Drive-shaped branch through
every one of them.

**It is not slower than the NAS.** Measured on a 22MB FLAC through the real
player:

| | NAS over SMB | Google Drive |
| --- | --- | --- |
| time to sound | ~1400ms | 1208ms |
| 20 held skips | 221ms, median 0 | 232ms, median 0 |
| sustained download | 6-8MB/s | 11.8MB/s |

**A Drive request costs about 700ms whatever is in it.** 64KB takes 743ms and 8MB
takes 1559ms, so the round trip is nearly all of it and the bytes are nearly
free. Windows asks a cloud provider for roughly 256KB per callback, so answering
each one with its own request is 700ms per quarter megabyte — about three minutes
to read a 36MB track. Anyone wiring `FETCH_DATA` straight to the API ships
exactly that.

So **the callbacks never talk to Drive if they can help it**. `DriveFileCache`
pulls the file down in 4MB chunks, two requests at a time (11.8MB/s, against 5.1
for isolated single requests and 5.4 for four at a time), and the callbacks are
memory copies: measured over a 232MB track, **891 callbacks, 888 served from
memory**. This is `StreamingSource`'s argument applied to a different remote,
and it is the same argument because it is the same problem. Two streams is the
setting — unlike SMB, where parallel readers bought nothing at all.

**One Drive download at a time, and that has to be wired to something.**
`DriveMount.ReleaseAllBut` is what makes "starting another disposes the last"
true, and for a long time nothing called it — the same way the notification
catalogue sat inert. So reading a single byte of a file, which a tag read or a
folder listing does, started a download of the whole file that ran to
completion, and every file ever touched kept one alive. The pool's budget capped
the *memory*, which is what made it invisible; nothing capped the network.

**And a cache is never made through `ConcurrentDictionary.GetOrAdd`'s factory
overload.** It may run the factory more than once for one key and keep a single
result, and a `DriveFileCache` starts downloading *in its constructor* — so each
discarded loser carried on pulling its file down for ever, holding a
cancellation source nobody would ever cancel and evicting the chunks of the
track actually being played. These callbacks arrive on filter-driver workers,
several at once, so it is not a theoretical race. The value overload is used
instead, and whichever thread loses disposes the instance it made.

**Memory is chunks under one budget** (`DriveCachePool`, `GoogleDrive.CacheMegabytes` = 1024, fixed,
1GB), not a buffer per file. A whole-file buffer lets the largest thing ever
opened decide the ceiling, and there is a 232MB track in this very Drive. Chunks
also let a file bigger than the whole budget still stream: the beginning is
dropped as the end arrives, which for something being played start to finish is
exactly the right thing to lose. Eviction is LRU by *use*, so reading chunk 0
protects it and the next eviction takes chunk 1.

### Five things that look like they should work and do not

None of these report an error. Each one produces a drive that mounts, opens, and
is quietly wrong.

- **`MARK_IN_SYNC_ON_ROOT`** says the root is in sync, which Windows reads as
  already populated. The drive comes up empty.
- **The same flag on a directory placeholder**, one level down, with the same
  result for everything inside it. Files want `MARK_IN_SYNC`; folders must not
  have it.
- **`CfConvertToPlaceholder` on the sync root** — the call that would let the root
  record that it had been populated — is refused with `0x8007017C`, "the cloud
  operation is invalid". The root can never be a placeholder, so it is seeded by
  us at mount and `DISABLE_ON_DEMAND_POPULATION_ON_ROOT` stops Windows asking a
  question it can never record as answered. Without that flag the root is
  re-asked **every 220ms for as long as the mount is up**.
- **`CF_POPULATION_POLICY_FULL`** refuses `CfCreatePlaceholders` on the root
  outright, whatever the register flags.
- **A transfer that arrives late** gets `0x8007018E`, "the cloud operation was
  canceled by user". Windows reads ahead speculatively and cancels what it turns
  out not to need, so this is ordinary traffic. Log it as a failure and every
  session looks broken.

**A stuck sync root is worth *trying* to reclaim, and some of them still will
not go.** `PickRoot` used to only ask whether a candidate directory was empty,
and the code that empties one — `ClearContents`, which works because the
provider is connected — ran on the root `PickRoot` had already accepted, which
by definition was empty. So the one directory that needed clearing was the one
thing that could never be cleared. `DriveMount.TryReclaim` stands a provider up
over a leftover root purely to service its own deletes (register, connect,
clear, disconnect, unregister — no listing, no letter, ~13ms) and it recovers
anything that is merely un-serviced.

What it does not recover is a placeholder whose sync root registration is gone
for good. Measured on one: `GetFileAttributesW` fails with **203**, which is
`STATUS_IO_REPARSE_TAG_NOT_HANDLED` coming through the NTSTATUS-to-Win32 table —
the reparse tag has no filter willing to answer for it. `RemoveDirectory`,
`icacls`, `fsutil reparsepoint query` and `attrib` are all denied, and
re-registering a fresh sync root at the same path does not adopt it, because the
placeholder references a sync root id that no longer exists. That is the end of
the road from user mode; stepping aside to `GoogleDrive-2` remains correct, and
the leftover is inert — it costs a name and a folder in `%APPDATA%`, nothing
else. Do not add a fight with it; the reclaim attempt is the whole of what is
worth trying.

**Populating works, and the evidence that it did not was our own harness.** For a
long time subdirectories appeared never to raise `FETCH_PLACEHOLDERS`. They do —
about 1.3s for an untouched folder, from an unrelated process, so File Explorer
and a command prompt get a working tree too. The harness had been walking the
tree within milliseconds of creating the placeholders *and* calling
`GoogleDrive.Populate` first, which marks a directory populated and stops the
callback ever being needed. It was shadowing the mechanism it was testing.
`Populate` is kept as belt and braces: navigation knows a folder is about to be
listed and can pay the listing up front, and it costs nothing when the callback
got there first.

### Copying inside Drive, which is one request

**This was refused outright, and the reason given was wrong.** The message was
"Copying inside Google Drive is not supported yet. Move works, or copy out and
back in", sitting on a comment that copying inside Drive "means downloading and
uploading the same bytes". It does not. Drive has `files.copy`, which duplicates
server-side: **no bytes cross this machine**, a four gigabyte video costs the
same one request as a four kilobyte note, and the advice in the refusal was to
send those four gigabytes down the link and back up it to avoid a request that
moves none.

`DriveClient.Copy` is that request, through `SendWithRetry` like every other
write that is not a stream of bytes — a copy is cheap for us and expensive for
Drive, so it is exactly the one most likely to come back 429 in a folder being
duplicated a file at a time.

**Drive will not copy a folder**, and that is its rule rather than a corner cut
here: `files.copy` refuses one. So `GoogleDrive.CopyOnDrive` makes a folder and
asks the same question about everything in it, recursively. The listing it walks
is Drive's own (`ListChildren`) rather than the placeholders, so a folder nobody
has ever opened on this machine copies exactly as well as one that is on screen —
and below the top level there are no local paths at all, because the folders being
made have never existed here, which is why the recursion carries a Drive id and
keeps the local path only as far as placeholders have actually been placed.

**The name is settled before the request, not after it.** Two files with one name
in one folder is perfectly legal in Drive and impossible on a drive letter: the
placeholder for the second has nowhere to go. And a copy is the one operation
whose destination nearly always already holds the name it is bringing —
duplicating a file in place is the ordinary case — so this path, which previously
did not exist, is also the path where the paste conflict setting matters most:

| setting | what a clash does |
| --- | --- |
| Auto-rename | `NameRules.UniqueAmong` against the destination listing |
| Skip, Fill gaps | left alone |
| Overwrite | copied under a free name, *then* the old one trashed and the new one renamed |

That order for Overwrite is the whole of it: the other way round leaves a moment
with the old file trashed and the new one not yet there, and a copy that fails in
that moment has taken something away and put nothing back. Trashed rather than
deleted, because the Drive bin is the recycle bin here.

**Copy and move inside Drive share `DriveToDrive`.** It has no thread count,
because nothing here moves bytes. It does have a progress window with Cancel, and
it counts as a running transfer, because a tree is one request per item: minutes
for a big one, and quitting in the middle has to ask. It stands down only the
watchers of the folders it changes.

### Clicking through tracks made the next one fail

Reported as: a random track on the letter did nothing, "could not play"
arrived about thirty seconds later, and nothing else would play until then.
Found with `dotnet-stack` on the running application and a probe of its own
(the app's sources, a copy of the token, Google read directly). The file was
never the problem: downloaded straight from Drive, it decoded perfectly.

Three things added up:

- **A cold read waited for a whole 4MB chunk.** On the link that day, one 4MB
  request took 4 to 6 seconds. A decoder opening a FLAC reads the head, the
  middle and the end, so an open waited for three chunks.
- **Every track clicked kept downloading.** A `StreamingSource` filler stopped
  only after the 4MB read it was in, and that read was itself seconds long. Its
  `DriveFileCache` pump went on pulling the whole file. One sample had seven
  fillers and ten fetch callbacks in flight, all splitting one link.
- **So the track actually wanted opened too slowly**, went past the decoder's
  budget, and failed. Enter holds the play gate while it waits, so everything
  after it queued too.

The fixes:

- A read whose chunk is not there waits `ChunkPatienceMilliseconds` (400) for
  it. It then fetches just its own bytes (at most `DirectBytes`, 1MB), once per
  chunk. Measured cold: 1.2 to 1.7 seconds a region, against 4 to 6. Once per
  chunk, because a copy reading at the download's edge would otherwise make a
  small request per callback, which is the 0.2MB/s the class comment warns
  about.
- A pump stands aside while its file has gone unread for 3 seconds and another
  file has been read since (`DriveCachePool.NoteRead`). A warmed file that
  nothing else has overtaken keeps downloading.
- The filler reads asynchronously with a token, and `Release` and `Dispose`
  cancel it mid-read.

A probe that exits while its own read of the letter is still pending becomes
the stuck process described below. One did during this. Give a probe that plays
from the letter time to stop before it exits.

### A failed read has to name the range, or the reader waits for ever

**This was the root of every "unkillable process until a reboot".** `Fail`
answered a read it could not serve with `TRANSFER_DATA`, a failure status, the
unaligned offset, and a length of **zero**. Windows refuses that, and a refused
failure is no answer at all: the reader stays parked in the kernel.

What that caused:

- Every read that failed here was left pending: a Drive request timing out on a
  busy link, a callback past its deadline, a file with no identity.
- A player's download thread piled up behind each one. One sample showed a dozen
  of them.
- Any process ended while so parked never finished exiting: `HasExited` true,
  one thread in an `Executive` wait, nothing able to end it. Disconnecting and
  reconnecting the provider does not release it either. Only restarting Windows
  does.

`Fail` now passes `RequiredFileOffset` and `RequiredLength`, the range Windows
asked for. It logs if Windows still refuses. Proved with
`DriveMount.StandDownCheck`: a provider over a scratch folder, a separate
process in a plain blocking read of a cold file, its read failed and the process
killed mid-read. It was gone within seconds. Before the fix, the same test left
it stuck.

Built around that:

- **Callbacks carry the reading process** (`REQUIRE_PROCESS_INFO`).
  `StandDownReads(pid)` fails that process's reads, now and from then on, and
  returns once none has been in flight for 300ms. A cancelled copy calls it for
  robocopy (`RoboCopyEngine.BeforeKill`) before killing it.
- **Unmounting fails every read in flight at once** (`_closing`), not after its
  deadline, so nothing is pending when the provider disconnects. Folder listings
  are cancelled the same way.
- **`StreamingSource.ReadAt` retries a failed read for up to 20 seconds** rather
  than returning nothing. Nothing is how the end of the file looks, and Media
  Foundation ended the track on it: part of a song played, then silence.

### Permissions are asked before a move, not discovered after

Most of this account's 64 shared drives are view-only. Measured with
`DriveClient.RightsOf`: copying is allowed, but trash, rename, move and add are
not. A shared drive's own id reports itself as its `driveId`. A My Drive folder
answers `canCopy` false, because Drive never copies a folder as a unit.

- **Inside Drive**, `MoveOnDrive` requires `canAddChildren` on the
  destination. It also requires `canMoveItemWithinDrive`, or
  `canMoveItemOutOfDrive` when the source and destination drives differ.
  Otherwise it refuses with "copy it instead". It also refuses when the source's
  folder is unknown: the PATCH would then only add a parent, leaving the item in
  both places.
- **`CopyOnDrive`** requires `canAddChildren` on the destination, and `canCopy`
  for a file. A folder's `canCopy` describes its contents, which Drive refuses
  one by one.
- **Out of Drive**, a move ends by trashing the originals. `RunTransfer` asks
  `WhyNotRemovable` first, four requests at a time, and refuses the whole move
  before anything is copied. It used to copy, then fail the trash, then report a
  half-done move.

A copy from a shared drive into My Drive has always been `files.copy`, done by
Google, with nothing downloaded.

**The window reports what Google answers.** It used to be told only about the
items picked, so copying one folder read "0 of 1" until the copy finished.

- **Copies:** first `MeasureOnDrive` counts and sizes the tree from Drive's
  listings, four folders at a time, showing "Counting". Then `DriveCopyReport`
  hears each file as it is asked for and again when Google has made it, with
  its size. The window shows files, bytes, speed and time left.
- **Moves:** a move is one request per item picked, so a move counts those.
  `TransferProgress.ItemsKnown` shows "N of M items" without a byte total.

### The freeze in whatever you paste into

**Copying a Drive file put a placeholder path on the clipboard and started
nothing.** So the first byte the other application read was the first byte
fetched from Google — and a DAW importing a track reads synchronously on the
thread that paints its own window. The whole download therefore happened with
that window frozen, and Windows painted "(Not Responding)" over it.

Measured on `G:\Pogo - Absoblume.flac`, 16.8MB, same file and same link within
one minute:

| | time | rate |
| --- | --- | --- |
| cold, first read | 3,899ms | 4.1 MB/s |
| again, from the pool | 111ms | 143 MB/s |
| again, from the OS cache | 3ms | 4,053 MB/s |

4 MB/s is the sustained cold rate and it is linear in the file: a 50MB wav is
thirteen seconds of frozen application and the 232MB track is about a minute.
The bytes are not slow — the *timing* is.

So `MainForm.CopyToClipboard` calls `GoogleDrive.Warm`, which opens the cache
and lets `DriveFileCache`'s constructor start the four-stream pump. The seconds
somebody spends finding the other window are seconds of download that used to be
spent inside its frozen read. Scattered reads were never the problem and a
measurement says so: ten seeks across a cold 15MB file cost 1,017ms in total,
because the pump had already pulled all four of its chunks.

**Only on a copy, never on a cut.** A cut pasted back inside Drive is a move,
which is one metadata request that reads no bytes. Copy has the same ambiguity —
`DriveClient.Copy` is server-side — which is why a copy always warms the file (`if (!cut) Drive?.Warm(paths)`; no longer a setting).

**Both bounds on it are about the thread that took the copy**, and this
application has already had a thirteen-second window freeze out of doing a
per-file lookup for three thousand selected items. `Warm` stops looking after
`MostWarmCandidates`, `GoogleDrive.Warm` hands the whole thing to a worker with
the selection copied first, and `WarmPlan` — pure, and tested for real rather
than by reading the source — applies the two that decide what is worth starting:
never more files than `MaxOpenFiles`, since a fourth calls `KeepRecent` and
disposes the first; and never one bigger than the pool, which would evict
everything warmed beside it and still arrive cold.

**And a byte budget only gives memory back when something else wants it.** A
file warmed and then walked away from sat there for as long as the mount was up,
so `SweepIdle` releases anything nothing has read for `GoogleDrive.CacheIdleSeconds` (120, fixed)
(120). It refuses to run while any callback is in flight: releasing a cache
cancels its downloads, and a sweep landing inside a fetch callback would fail the
read that callback is answering, which is the copy that stops dead for no visible
reason this file already carries scars about.

### Searching, and why Drive is searched by asking rather than by walking

F1, `MainForm.SearchHere`. The dialog is `PromptForm` — a labelled field with OK
and Cancel that opens with the caret in it — rather than a second dialog class
saying the same thing. One rule does the matching: case-insensitive, anywhere in
the name, and **the name includes the extension**, so "love" finds `Lovesick.flac`
and ".flac" finds every track. A name box and a separate extension box would be
two things to tab past to do the one thing anybody wants.

**Results are the pane, not a window.** `Pane.SearchTerm` is the whole of the
mode: non-null means the list is showing results, `CurrentPath` stays the folder
that was searched so paste and refresh still have a real answer, and Escape goes
back. Three things follow and each was a way to lose the results silently — the
folder watcher rebuilding the pane from the folder, F5 reloading it, and
`RememberPosition` overwriting the row the search started from with one belonging
to a folder three levels down.

**The row says where the hit came from, after its name.** `Lovesick.flac, in
2019`. After, because a row beginning with its folder means pressing L no longer
jumps to the L files — the location has to be in the sentence without being in
front of it. Enter goes to where the thing lives and lands on it rather than
opening it: a result is an answer to "where did I put that", and there is
somewhere to press Enter a second time if opening it was what was wanted.

**Drive is searched through Google.** Walking the letter is the obvious way and
it is hopeless — populating a cloud folder is one network listing at about 1.3
seconds, and this account has **1347 folders**, so a recursive walk is about
twenty-nine minutes. Measured against the real account instead:

| | |
| --- | --- |
| `files/root` | 637ms |
| `name contains 'love'` | 214 hits, 961ms |
| every folder in the account | 1347 folders, 2900ms |
| **a whole-account recursive search** | **3.9 seconds** |

Two requests and a root lookup, whatever the shape of the account. The folder
listing is the one that makes scoping cheap: **Drive's query language has no
recursive form**, so "under this folder" has to be worked out here, and the
route that suggests itself — a `files.get` per hit's parent chain — is 700ms per
folder. One listing of all folders puts the whole graph in memory and every chain
then resolves for nothing. `DriveSearch.ChainUnder` is that arithmetic, kept pure
so the awkward cases (a hit outside the root, a chain that leaves the account, a
graph that loops) are tested against a graph built by hand. It is cached for five
minutes, because folders are made far more rarely than files and it is three
quarters of what a search costs.

**A hit's path comes from its id, never from the name Drive returned.**
`Sanitise` and `Unique` have both had a go at everything on the letter, so a file
Drive calls `AC:DC.flac` is `AC_DC.flac` here and the second `song.flac` in a
folder is `song (2).flac`. Building a path out of the Drive name produces results
that do not open, which is the same trap `DrivePlaced` exists to keep the pane out
of. `DriveMount.ChildPathForId` matches on the id against the folder's own
listing, and only the folders that actually hold a hit are populated — a search of
two thousand tracks that finds four of them pays for the listings of the handful
of folders they are in, not for the tree.

**The account root is asked for, not inferred.** `"root"` is an alias Drive takes
in a query and never the id it puts in a `parents` array, so a chain cannot be
recognised as having reached the top without joining the two up. The tempting
inference — the parent that is not itself a folder in the listing — picks the
wrong answer for any account with a folder shared into it, whose parent is
equally absent. `DriveClient.RootId` is one request and no guessing.

**Two bugs in the first version, and the first one is the lesson.** The live
probe resolved all 214 of its hits and reported perfect health, and the feature
was still returning almost nothing: *resolving a chain* and *materialising a
path* are two steps and only the first was being exercised. The mount can only
say what is inside a folder it has populated, and the walk populated each folder
in order to find the next one — so the folder at the bottom, the one the hit
actually lives in, was never populated and the lookup inside it found nothing.
Every result below the top level was dropped silently. The fix moved that walk
into `DriveSearch.PathForChain`, which takes the mount's two operations as
delegates purely so the ordering between them can be tested against a fake that
refuses to answer for an unlisted folder — which is what the real mount does.

A probe that exercises the clever half of a feature and not the plumbing is a
probe that will report a broken feature as working.

**And it did its Populate on the UI thread.** `SearchAsync` is awaited from a
keystroke handler, so without `ConfigureAwait(false)` every continuation comes
back to the UI thread — and what runs after them is `Populate`, a *synchronous*
Drive listing at about 1.3 seconds each. A search touching five folders would
have frozen the window for six seconds, which is the rule this codebase bends
for nothing. `LoadEntriesAsync` already wraps its own `Populate` in a `Task.Run`
and says why; the same hazard came back through a different door.

**The local walk is an explicit stack, not recursion**, because a deep tree is a
stack overflow and that cannot be caught. It is cancellable at every *entry*
rather than every folder — a token only looked at between directories is a cancel
that is not heard until the current one finishes, and this walks whatever folder
somebody happened to be standing in. It does not follow reparse points, for the
reason `DriveUpload.Survey` does not. And it stops at `FileSearch.MostResults`
and **says so**: a list that stopped at the cap looks exactly like a complete one,
and somebody who cannot see the scrollbar has no other way to find out.

### Hydrating a placeholder to disk does not work here, and the reason is our own policy

Worth writing down because it is the obvious thing to reach for and it fails
slowly rather than loudly. `CfHydratePlaceholder` on a file on the letter:

| asked for | result |
| --- | --- |
| the whole file, cold | `0x800701AA` after **120,741ms** |
| the whole file, already fully in the pool | `0x800701AA` after **60,434ms** |
| **64KB**, already fully in the pool | `0x800701AA` after **60,441ms** |

`0x800701AA` is `ERROR_CLOUD_FILE_REQUEST_TIMEOUT`. The third row is the one that
identifies it: sixty seconds to fail at putting 64KB on disk, for a file whose
every byte was already in memory, so the *fetch* is not what is failing. Nothing
is persisted either way — the attributes stay `RECALL_ON_DATA_ACCESS | OFFLINE`
before and after, and `CfDehydratePlaceholder` then returns `S_OK` in 3ms because
there is nothing to dehydrate.

It is `CF_HYDRATION_POLICY_MODIFIER_STREAMING_ALLOWED` on the sync root: the
platform is told it may stream rather than hydrate, so there is no on-disk
hydration path for an explicit request to use. Taking the flag off is not the
trade it looks like — it would make *every* read of *every* Drive file pull and
persist the whole file, which is the player's seeking and the disk both. The
memory pool is the answer, and 111ms is fast enough that nothing is being given
up for it.

What this also means: nothing an external program writes into the letter reaches
the account. Only `FETCH_DATA`, `FETCH_PLACEHOLDERS` and `CANCEL_FETCH_DATA` are
registered — no `NOTIFY_*` — so a `.reapeaks` file, a render or a project saved
into `G:` stays a local file in the sync root and is deleted by the next mount,
exactly as New file and New folder used to do before they were given Drive halves.

### Writing to Drive

**The small-upload path had no retry, and it is the one that runs a million
times.** `DriveWrites.UploadResumable` has retried since it was written —
`MaxUploadAttempts`, five *resumes* from Google's own byte count. `UploadWhole`,
which takes everything under `SmallUploadBytes` (5MB), sent one request and
threw whatever came back. That is the wrong way round: a resumable upload is one
request for a large file, and the small path is one request *per file*, so a
folder of a million small files is a million chances for a service that is right
99.99% of the time to say no a hundred times.

It did. A real upload lost `BootSound.apk` to a **502 whose own body reads
"Please try again in 30 seconds"** — a file reported as permanently failed on
the strength of an answer that said the opposite.

`MaxSmallUploadAttempts` is six, with exponential backoff and jitter. Three
things in it worth keeping:

- **Only 408, 429 and 500/502/503/504 are retried** (`WorthRetrying`). Those
  mean "not now". A 403 for quota, a 404 for a deleted parent and a 401 for a
  stale token are about the request rather than the moment, and retrying them
  turns one clear failure into six identical ones and a longer wait.
- **The delay is jittered** (`RetryDelay`). Four upload streams run at once and a
  folder of small files puts all four through the same bad moment; a fixed
  backoff brings all four back at the same instant to fail together, which is
  how a blip becomes an outage.
- **The stream and the request are rebuilt inside the loop.** A stream already
  read to the end cannot be sent again and neither can an `HttpRequestMessage`
  that has been sent once — a retry reusing either fails for a reason unrelated
  to the one it is retrying, and looks exactly like the server refusing twice.

**A name is not a key on Drive, and "keep both" believed it was.** Google Drive
permits two files with the same name in the same folder — a name is a property
there, an id is the key. `DriveUpload.NeedsConflictCheck` excluded `AutoRename`
on the reasoning that under "keep both" the answer to a collision is the same as
the answer to no collision, so nothing needs listing. That is true of a
filesystem, where you *have* to pick another name, and false here: nothing was
listed, nothing was found to clash, and the upload made a second file called
exactly what the first one is called. Uploading a folder twice produced two of
everything, silently, with nothing in the listing to tell a pair apart.

It also made `Ask` look broken, through two silent assumptions in a row.
`CollidingNames` answered "nothing clashes" when the destination could not be
*listed* — the same answer as "I looked and found nothing" — so `Ask` resolved
to `AutoRename` before the upload started, and `AutoRename` then checked nothing
either. A setting explicitly set to Ask never asked.

Both halves are fixed. `NeedsConflictCheck` is always true on Drive, and
`CollidingNames` hands back whether it managed to check at all; a Drive
destination that would not list is asked about rather than assumed away, with
`ConflictForm` saying in as many words that Drive would not say what is there.

**And an error body is not a message.** Google answers a 502 with a full HTML
page — doctype, stylesheet, a link to a picture of a robot, about seven hundred
characters — and that went into the failure dialog whole. On screen it pushed
the other nineteen failures off; read aloud it is a minute of markup before the
sentence saying which file it was. `Explain` takes the `error.message` out of a
JSON body, names an HTML page rather than quoting it, and caps anything else.
`MainForm.ShowErrors` caps each line again at 300 characters as a second line of
defence — from the *end*, because the file's name is at the front and that is
the half worth keeping.

**The OAuth client is compiled in, and the file still wins.** `BuiltInCredentials`
carries the client id and secret for project `dogwood-seeker-430323-g8`, so
connecting Drive is one button rather than a trip to the Cloud console for
somebody who was handed this. A desktop client's secret is not a secret and
Google says so — it ships inside the executable where anybody can read it, and
what secures the flow is PKCE and the loopback redirect. What embedding it
actually costs is the project's **OAuth user cap**: everyone signing in with this
client counts against one hundred, for the life of the project, and that figure
cannot be reset. Fine for a handful of people; wrong for anything published, and
the answer then is for each person to bring their own client — which is what
`GoogleAuth.ReadClientJson` is still there for, and why a file on disk takes
precedence over the built-in.

**Publishing status is "In production", and that is what stops the weekly
sign-in.** In Testing, Google revokes refresh tokens after seven days. Production
needs, for a restricted scope, a home page, a privacy policy and terms on a
registered authorized domain — they are at `ce2004.github.io/explorer-native-site`
and the domain is registered in the console. Verification is *not* required and
has not been done, which is why consent shows the "unverified app" interstitial
once per user. `github.io` was accepted as an authorized domain despite being a
public suffix; the console asks for the full subdomain, not the bare one.

**Signing in and out are buttons on the Drive page, not a paragraph about a JSON
file.** That page used to explain where to put `client_secret.json`, which is an
accurate instruction and a hopeless one for anybody who did not create the Cloud
project. `GoogleDrive.HasSavedSignIn` and `ForgetSignIn` back the two buttons.
Signing out is deliberately *local*: it deletes the token file and does not call
Google's revoke endpoint, because "sign out of this computer" and "withdraw this
application's access to my account" are different things and a button doing the
second while saying the first is a surprise on somebody's other machine. The
account page is named on the page for anyone who wants that.

**Read-only was a deliberate choice and it is over.** The scope is now
`https://www.googleapis.com/auth/drive`, because copy, move and delete were
asked for. The trade is real: a bug here can damage the account, where before
the worst it could do was fail to show something.

**A refresh token is only good for the scope it was granted under.** A token
saved under the old read-only scope keeps refreshing happily and then fails
every write with "insufficient permissions" — which reads like a broken upload
rather than a stale sign-in. `GoogleAuth.ScopeVersion` is in the token file
name, so changing the scope makes the old token simply not found and consent is
asked for once. Bump it whenever the scope changes.

**Every command that writes has to know the letter is not a disk, and three of
them did not.** Delete and paste both branch on `Drive.Owns`. New folder, New
file and Rename did not — they were `Directory.CreateDirectory`, `File.Create`
and `File.Move` against a path inside the sync root. Nothing failed. The
directory was created, the file was created, the placeholder was renamed, and
each command announced that it had worked. What actually happened:

- **None of it reached the account**, so none of it appeared: the pane is built
  from the Drive listing (see `_listings`), and a local file the listing has
  never heard of is not in it.
- **The next mount deleted it**, because `Start` clears the sync root — that is
  how a leftover placeholder tree from a killed run is dealt with. So a new file
  was invisible from the moment it was made and gone by the next launch, taking
  anything typed into it.
- **Rename was worse than a no-op**: the account keeps the old name, so the next
  listing puts it straight back, *and* the renamed placeholder is left on disk
  under a name no listing mentions.

`CreateFolderIn`, `CreateFileIn` and `RenameOnDrive` are the Drive halves.
Creating an empty file is one metadata request with no body, exactly like
creating a folder with a different mime type — uploading zero bytes would cost a
second round trip to say the same thing. Renaming is `MoveOrRename` with a name
and no parents, then `RemoveOne` and `PlaceOne` locally, which is why that
parameter existed with nothing calling it and why `drive.renamed` sat in the
catalogue marked "not raised". A test reads `MainForm.cs` and checks all three
branches are there, because MainForm needs a message loop and is not compiled
into the suite.

**And "New folder" twice has to make two folders.** `CreateFolderIn` merges into
an existing folder of that name, which is right for a paste and wrong for F8 —
so the name is made unique against the folder's listing first.
`NameRules.UniqueAmong` is that rule, shared with `FileOperations.UniqueName`,
because "already taken" is the filesystem on a disk and the listing on the
letter, and two copies of "a folder has no extension whatever its dots say" is
two copies to get wrong.

**The paste conflict policy applies here too, and per file rather than per
source.** Drive lets two files in one folder share a name, so an upload that
ignored the setting quietly made duplicates — which is right for "keep both" and
wrong for the other three. `DriveUpload` asks each destination folder what it
holds (one listing, cached, and *only* under a policy that needs an answer, so
the ordinary upload costs exactly what it did before) and then skips, renames or
replaces. Replacing is two steps in this order and no other: **upload the new
copy, then trash the old one.** The other way round loses the file whenever the
upload fails. The new placeholder has to be called `song (2).flac` while the old
one still holds the name, so `Replace` drops both and places it again once the
name is free — local work only, and the difference between "replaced" and "a
folder with a number in it and nothing to say why".

Verified end to end against the real account, all three answers: replace leaves
one file with the new bytes under the plain name, skip leaves the old bytes and
uploads nothing, keep both leaves two files in Drive shown as `song.flac` and
`song (2).flac` on the letter.

**Copying *out of* the drive goes through robocopy, with three differences.**
All three were found by copying one track into Downloads and looking at what
landed, after "I can't copy items from Google Drive, the progress bar is
breaking, and it just won't go to Downloads":

- **The Offline flag is stripped.** A placeholder carries `FILE_ATTRIBUTE_OFFLINE`
  and robocopy copies attributes, so the file arrived with the right bytes, the
  right size and the Offline flag — which Explorer draws as unavailable and many
  programs refuse to open. It looked exactly like nothing had been copied.
  `cloudSource` adds `/A-:O` for files and `/DCOPY:T` for folders. The suite sets
  Offline on an ordinary file to reproduce it, and proves the premise first — that
  robocopy really does carry the flag without the switch.
- **A move is a copy and then the Drive trash.** Nothing here answers the
  `NOTIFY_DELETE` callback, so robocopy's `/MOV` deletes the local placeholder
  and the file stays in Drive, back on the next mount — a move that silently
  did not. `FinishMoveOutOfDrive` trashes the originals only after every file
  copied, never one the conflict policy skipped, and nothing at all if more were
  skipped than the outcome remembers by name.
- **The progress comes from the mount.** Robocopy writes nothing until Drive has
  delivered and its "New File" lines arrive through a buffered pipe after the
  file is finished, so the window measured **0% for four and a half seconds on
  six tracks** and then jumped a whole file at a time, one name behind. Every
  byte robocopy gets comes through `OnFetchData` in this process first, so
  `DriveMount.Watch` lets a transfer see what has been served for its own paths
  and `RoboCopyEngine.WithDownloads` shows the larger of that and robocopy's
  figure, never going backwards. The fetch path marshals nothing unless a watch
  exists.

  **It counts the union of the ranges served, not the furthest byte.** It
  shipped as the furthest byte, on the reasoning that a file is read front to
  back, and the window then said "100 percent, less than a second" for a minute
  while robocopy was still pulling the file down: Windows reads ahead, and a
  scanner or a WAV reader looks at the end of a file early, so one read of the
  last four megabytes made a 65MB file count as finished. `FetchWatch.Ranges`
  merges intervals; the test reproduces the early read of the tail.

`MainForm.TransferThreads` reads one file at a time out of Drive, and that is
what keeps the rest working: `DriveMount.MaxOpenFiles` is three, and ten robocopy
threads over three caches evict each other, restart downloads from zero, and
time the callbacks out.

### The player that went silent for good, and the process that cannot die

"Nothing plays" on the Drive letter was four waits with no end, chained, and it
was found by reading the live application with `dotnet-stack report -p <pid>`
(installed globally; it reads managed stacks without touching a window):

1. A `FETCH_PLACEHOLDERS` callback parked on a folder listing that went through
   the offline retry loop, with no deadline.
2. `StreamingSource.Read` read ahead of its download **while holding its lock**,
   and on the Drive letter that read is served by this same process's provider.
3. `Dispose` needs the same lock, so changing track waited on the read.
4. `TrayApplicationContext` holds `_playGate` across `Play`, so every Enter
   after that queued behind it. Measured: nine Enter presses parked on the gate.

Nothing timed any of it out. The fixes, each guarded by a source check in
`DriveCopyTests`: the read ahead of the download is a positional
`RandomAccess.Read` outside the lock; both provider callbacks carry a deadline
(`PlaceholderDeadline` 45s, under Windows' own 60, and `DataDeadline` 90s,
because the keep-alive otherwise holds Windows off indefinitely); and
`DriveFileCache` waits for a chunk with `WaitAsync(token)` so the deadline is
actually heard. A failed callback is an ordinary read error that robocopy
retries and the player reports; a callback that never returns is none of those.

**A process that is reading a placeholder when it exits may never finish
exiting.** Measured twice: `HasExited` is true, the runtime is gone, one to four
threads sit in a kernel `Executive` wait, `Stop-Process` does nothing, and it
stays until a reboot. The worst case is the application itself, because it is
both the provider and a reader of its own placeholders: its read waits for a
provider that died with it. It holds no lock and no letter once in that state —
the next copy starts and mounts normally — but **anything it inherited stays
open**. A probe started with `Start-Process -RedirectStandardOutput` inherits
the starting shell's handles, so a probe that gets stuck like this hangs the
terminal that started it. Probes that touch `G:` start through the shell
(`UseShellExecute`), write their own output file, and inherit nothing.

The install report had the same blind spot from the other side. It judged "has
the old copy stood down" by whether the installed path could be opened — true at
once after a rename, because the old process holds the *retired* file. It now
watches the processes themselves and the single-instance mutex separately
(`StandDown.Gone`, `LetGo`, `StillRunning`), and starts the new copy only once
the mutex is free.

**Nothing may be reading the letter when the provider goes, and that is an
ordering rule in `Quit`.** `_drive.Dispose()` was the first thing quitting did,
while the player could still be reading a track off `G:`. A read of a
placeholder is answered by this process, so pulling the provider out from under
one leaves a read waiting for an answer that can never arrive — and Windows will
not let a process exit with I/O outstanding, so the process stops halfway out:
exit code set, runtime gone, threads parked in a kernel wait, `taskkill`
refused, gone only after a reboot. The player and the prefetcher are disposed
first, `StreamingSource.WaitForReads` waits for anything still in flight, and
`DriveMount.Dispose` waits for callbacks it is still answering
(`WaitForCallbacks`) before disconnecting. Both waits are bounded at five
seconds: the alternative to a short pause at the end of the process is a machine
with something unkillable on it.

**Never probe this application through its window.** UI Automation reads froze
it, and a recursive listing of `%APPDATA%\ExplorerNative` walked the sync root,
which is one Drive request per folder. Logs, `settings.json` and `dotnet-stack`
answer every question those were asked.

**Deleting goes to the Drive trash, never to files.delete.** The Recycle Bin
cannot hold a placeholder — there is no local file to move — and removing the
placeholder alone would leave the file in Drive with nothing on this machine to
show for it. Trash is the same bargain the Recycle Bin makes and it is
recoverable from drive.google.com.

**The placeholder is removed only after Drive has agreed.** The other order
hides a file that is still there if the trash call then fails, which is the
worst possible outcome: it looks deleted and is not.

**Verified end to end, twice**, because "the code path exists" is not the same
claim. Upload a file through the app, delete it, confirm, and then **restart** —
a fresh mount re-lists from the Drive API, so a file that is still on the server
comes back. Checking only that it vanished from `G:` proves the placeholder went,
not the file. The confirmation dialog is Drive-aware and says where it is going:
*Really move "song.flac" to the Google Drive trash?*

That sentence was built as `Really {verb} {what}?` and read "Really send to the
Recycle Bin "song.flac"?" — the object after its own destination, in two of the
three cases. It is assembled per case now. Worth remembering generally: a
sentence made by concatenating a verb phrase and a noun will put the noun last,
and English usually wants it in the middle. This one is read out loud.

**Nothing is buffered on the way up. The file is streamed.** It is opened, handed
to the request body, and goes disk → socket through a 256KB copy buffer and
nothing else — no temporary file, no chunk in memory, no whole-file array.
Measured against the real account, 400MB of known bytes verified at three
offsets including one a kilobyte from the end: **21.5 seconds at 18.6MB/s, peak
working set 59MB, 3MB of managed allocation for the entire run.** A gigabyte and
a kilobyte cost the same memory to send.

It used to go up in 8MB chunks, each read into an array and each its own
request, and that cost twice: an 8MB allocation per upload, and a round trip's
latency every 8MB — 128 of them for a gigabyte, with the link idle for most of
each one while Google answered. That version measured 13.9MB/s. One streamed
request keeps the socket busy from the first byte to the last.

**Resumability was not given up for it, only moved.** A gigabyte cannot be sent
again because a connection dropped at eighty percent, so the session is still
resumable — but the asking now happens when a request *breaks* rather than
128 times on every upload that goes perfectly. `UploadResumable` catches the
failure, asks the session how much it actually holds
(`Content-Range: bytes */total` with an empty body), seeks the file there and
carries on. What used to be a cost on every upload is a cost on the ones that go
wrong.

**Trust Google's byte count, and rewind the file to match.** Trusting the count
alone fixes the *label* on the next request and leaves the *source* where it was.
The reader has already walked past the bytes Google says it never received, so
the rest of the file lands at an offset nothing checks — a file of exactly the
right length, with a hole in it, reported as a successful upload.
`source.Position = sent` before every attempt is the other half; it is a no-op on
the normal path and the whole point on the one the resumable session exists for.

**A 308 with no `Range` header means Google holds nothing**, and that is the safe
reading as well as the documented one: starting again from zero costs bandwidth,
and starting from a number it never confirmed costs the file.

**Small files skip the session, and it buys a request rather than a round trip.**
The obvious argument — a resumable upload is two requests and a multipart one is
one, so small files are twice as slow — is wrong here, and the measurement is
worth keeping because it is so plausible. Eight 64KB files, one after another:
**17.2 seconds through multipart and 17.5 through a resumable session**, medians
1345ms and 1372ms, on a link whose per-file times ranged from 1.1 to 2.0 seconds
either way. Google's session creation is cheap and the connection is already
open. What it does buy is *requests* — two thousand small files is two thousand
rather than four thousand, against a per-user quota counted per minute and spent
by four streams at once — so `DriveClient.SmallUploadBytes` (5MB) stays.

**The upload client has no timeout, and a stall watchdog instead.** `_http` has
fifteen seconds because it sits on the path of a filesystem callback holding
somebody up. An upload is the opposite case: one streamed request carries the
whole file, so 400MB is twenty-one seconds of a request that is working
perfectly and a fifteen-second ceiling would fail everything over about 200MB.
`DriveClient._uploads` has `Timeout.InfiniteTimeSpan` and `SendWatched` asks the
better question — has anything moved in the last sixty seconds — reading the
clock the progress stream keeps. A stall is reported as a stall, not as "the
operation was canceled" on the screen of somebody who cancelled nothing.

**A cancellation is not a network failure.** `DriveHealth` counted a bare
`TaskCanceledException` as the network going away, on the reasoning that
`HttpClient` reports its own timeout that way — which it does, wrapped around a
`TimeoutException`, and the inner-exception walk finds that on its own. The bare
one means somebody cancelled a token, and that somebody is us: disposing a
`DriveFileCache` cancels the two ranged reads its download keeps in flight, so
changing tracks twice in quick succession produced four consecutive "failures"
with no request in between to clear them, and the application announced out loud
that Google Drive was offline while it was answering perfectly. `Send` also skips the health report entirely when the
caller''s own token is the one that fired.

**One token refresh at a time.** Every Drive request asks for an access token
and Drive requests run several at once by design. When the hour was up they all
saw an expired token in the same instant and all went to refresh it: several
simultaneous POSTs for the same thing, and had the refresh token expired too,
several browser windows racing to claim the same loopback redirect.
`GoogleAuth._tokenGate` serialises it with the check repeated inside, and a
caller with `AllowConsent` false waits only fifteen seconds for the gate — a
filesystem callback must never queue behind a consent page waiting on a person.
`GoogleAuth`''s own `HttpClient` carries the same fifteen-second timeout
`DriveClient` does, for the same reason; it was left on the default hundred.

**308 is not an error.** "Resume Incomplete" is the normal answer to every chunk
but the last.

**Drive has no move.** A file's parents are a property, so moving is adding one
and removing the other in the same PATCH. Two requests leave a window where the
file is in both places or neither.

**A move deletes the original only after Google has the copy.** The other order
loses the file when the upload fails on its last chunk.

**Writes go through paths, not ids, and that is the part that breaks.** The API
half is easy; the half that has to know which Drive object `G:\music\x.flac`
means is where a mistake deletes the wrong thing. `DriveMount` keeps a path-to-id
map filled in as placeholders are built — the id is *on* the placeholder as its
file identity, but reading it back means opening the file, and opening a
placeholder means hydrating it. Hydrating a gigabyte to find out what to call it
is absurd.

**A finished upload is placed, not re-listed.** `PlaceOne` adds the one new entry
so an uploaded file appears without paying for the whole folder again.

**And it places against the names already there.** `Unique` is given a fresh set
on the populate path because that path places the whole folder at once; `PlaceOne`
puts *one* entry into a folder somebody is looking at, and with an empty set the
name reads as free. Drive allows two files in one folder to share a name and NTFS
does not, so uploading a second `song.flac` is an ordinary thing to do that ended
with `CfCreatePlaceholders` returning "already exists" — after `_ids` had been
rewritten to point the *old* placeholder's path at the *new* Drive file. The next
delete on that path would have trashed the wrong one. Seeded from the folder's
own listing, the second one is placed as `song (2).flac` with its own correct id,
which is both true and visible.

**A folder that is already there is merged into, not duplicated.** Drive would
allow a second folder of the same name, and the result would be an album split
across two directories that NTFS could only tell apart by calling one of them
"album (2)". `DriveMount.ChildFolderId` is the check; pasting a folder onto a
folder of the same name means merge everywhere else in this application and it
means merge here. Files are the opposite — a duplicate name is a second file and
keeping both is the only answer that cannot lose one.

**A folder this application just created is marked populated.** It knows what is
in it: nothing. `PlaceOne(..., populated: true)` records that with an empty
listing, so the first visit does not pay for a Drive listing whose only possible
answer is one already in hand.

**Folders upload, and the missing half was never the API.** They were refused
for a long time with the right reason attached — sending a tree means creating
every directory in it and tracking which id each one got, and a tree that fails
halfway leaves something nobody asked for. `DriveUpload` is that tracking.
`EnsureFolder` is the whole of it: a `ConcurrentDictionary` of relative path to
`Lazy<Task<string?>>`, where `a\b\c` waits on `a\b`, which waits on `a`, which
waits on the destination. Each folder is created once however many files queue
behind it, and the chain is a DAG so nothing can wait on itself.

It is a `Lazy` and not a bare `Task` for the reason `DriveFileCache` learned the
hard way: `ConcurrentDictionary.GetOrAdd`'s *factory* overload may run the
factory more than once for one key and keep one result, which here would be a
second folder created in Drive and then abandoned. The value overload with a
`Lazy` never evaluates the loser's.

Verified end to end: a tree of five folders (one of them empty) and eight files
pasted onto `G:\`, the application restarted so the mount re-listed from the
Drive API, and every file read back through its placeholder and compared by
SHA-256. Eight of eight matched, five of five directories present, the empty one
included. Checking that it *looked* right on `G:` without the restart would only
prove the session remembered what it placed.

**Four files go up at once, because the cost is latency and not bandwidth.** A
Drive request costs about 700ms before it carries anything, so one file at a
time spends most of a small-file upload waiting. `DriveUpload.Streams` is 4 and
`FolderStreams` is 8 — folder creation carries no bytes at all, so the only thing
being overlapped there is the wait. Not higher: Google rate-limits a burst, and
every stream in flight is a file whose bytes have been counted as sent and might
yet not arrive.

**An empty folder is part of the shape.** `RoboCopyEngine` learned this for the
local path — a folder whose whole subtree was empty vanished with no error and no
failed count — and an upload that only walks files has exactly the same hole. So
`Survey` lists the directories separately and `Run` creates all of them before
the files, whether or not anything is going into them.

**And the walk does not follow a reparse point.** A junction is somebody else's
tree wearing this one's name: following one can circle for ever, and at best it
uploads a whole disk out of a folder that looked like it held six files.

**Bytes are counted as they are read, and taken back when the file fails.** That
is the only moment anything knows a byte moved, and it means a file that then
failed has contributed to the total. `DriveUpload.Unsend` removes it, because a
percentage that keeps the bytes of work that was undone is a claim rather than a
measurement.

**The path-to-id map must be allowed to answer "I do not know".** `Relative`
used to end with three guesses, the last two of which could manufacture a
plausible key from a path on an entirely different tree: it searched for the
root's *folder name* anywhere in the string — matching "GoogleDrive" inside
"MyGoogleDriveBackup" — and otherwise handed back the whole path lower-cased.
A manufactured key that happens to collide with a real one is indistinguishable
from a correct answer, and this map is what turns a path into the id that gets
trashed or moved. The folder-name match is anchored to separators now and
anything unrecognised returns a sentinel, which `KeyFor` turns into null and the
population callback reports as "not part of the mounted Drive".

### Interop, credentials and cleanup

**`CfApi.CheckLayout` is the same argument as `AudioPlayer.SelfCheck`.** Every
structure carries its expected size and they are compared before anything is
called. It has already earned this: `CF_SYNC_REGISTRATION` was written as 64
bytes and is 72 — `StructSize` and both identity lengths each leave four bytes of
padding, so the `GUID` starts at 56 — and the check caught it before a single
call. A wrong offset is a pointer read out of the wrong eight bytes, failing
somewhere unrelated.

**The file identity is the Drive file id.** That blob is the entire mapping from
"Windows wants bytes" to "which object in the cloud", and it is why nothing has
to be kept alongside the placeholder.

**Drive and NTFS disagree about names.** Drive allows characters NTFS refuses and
lets two siblings share a name; a name that cannot be written is a file that
silently never appears, and a duplicate quietly replaces the first. `Sanitise`
and `Unique` handle both, and `Unique` keeps the casing Drive gave a name rather
than folding it to whatever was seen first.

**Replacing is not the same as removing, and there are four ways to disagree,
not one.** `Sanitise` substituted the forbidden characters and then *trimmed*
trailing dots and spaces, which is correct and which for a name made only of
those leaves nothing at all — an empty relative name is a placeholder that can
never be created, which is the failure the whole function exists to prevent.
Drive is perfectly happy with a file called `..`, or `...`, or three spaces. Each
of these is now substituted rather than trimmed away, keeping the length so two
of them stay distinct:

- characters NTFS refuses, which was always handled;
- a name that trims to nothing;
- a device name — `CON`, `NUL`, `LPT9` — which is reserved *with any extension*,
  so the underscore goes on the stem (`CON_.flac`) and not the end, because the
  rule splits at the first dot and `CON.flac_` is still called `CON`;
- a name past 255 characters, which Drive does not limit and NTFS does.

The test is the shape to keep: a list of hostile names, sanitised, and every
answer put back through `NameRules.IsUsableName`. Three of these four were found
that way in one run.

**Google-native documents are skipped.** A Doc, Sheet or Slide has no byte size
in the API or anywhere else — that half of "Drive reports the size wrong" is not
fixable by anyone — so showing them as files that will not open is worse than not
showing them.

**Skipping them is also the one thing here that cannot lose data.** Everything
else in this section can: the scope is full `drive`, not `drive.readonly`, and
has been since copy, move and delete were asked for — see "Read-only was a
deliberate choice and it is over" above. A paragraph here claimed the opposite
for a while, and a stale reassurance is worse than none, because the whole point
of writing the trade down was to keep it in view.

**The client secret is not a secret and the refresh token is.** Google says so
for installed applications: the secret ships in the executable and the security
of the flow rests on PKCE and a loopback redirect. The redirect uses `127.0.0.1`
on a port taken at run time — claiming a fixed one means failing whenever
something else got there first. The refresh token is encrypted to the user
account with DPAPI through `crypt32`, and both live in `%APPDATA%\ExplorerNative`,
never in the build or install tree.

**Set `ContentLength64` on the loopback response.** Without it the browser is
left holding a response it cannot tell has finished and sits on a blank page,
while the sign-in has in fact already succeeded.

**A sign-in expires about weekly and that is normal.** Google expires refresh
tokens after seven days for an app that has not been verified, and verification
for a Drive scope means a third-party security assessment. So the expiry is
handled, not prevented: `GoogleAuth.AllowConsent` is true only on a deliberate
connect and false everywhere else, because that expiry otherwise surfaces on
whichever call needed a token next — which for a cloud provider is a filesystem
callback on a Cloud Files worker thread, and opening a browser from there hangs
the directory listing that asked for as long as the person takes to sign in.
Instead the listing fails, `SignInRequired` fires once, and the tray says where
to go.

**A sync root registration and a drive letter both outlive the process.** Proved
by killing a test run and finding the letter still mounted afterwards, pointing
at a folder full of placeholders with nobody left to hydrate them.
`GoogleDrive.SweepOrphans` runs unconditionally on every launch, like
`ReleaseOverreachingFolderClass` — somebody who simply switched the feature off
would otherwise keep the wreckage.

**A subst drive cannot report Drive''s quota, and never will.** The letter is an
alias for a *directory*, not a volume, so every free-space question Windows is
asked about it is answered by the volume that actually holds the directory. A
5TB Drive on a 500GB machine therefore lists as "431 GB free of 475 GB, Local
Disk" — the C: drive wearing a different letter — in File Explorer and in this
application alike. There is no fixing that without a real filesystem driver,
which is the thing this design exists to avoid, so `GoogleDrive` keeps
`QuotaUsed` and `QuotaLimit` from the About response that already proves the
token at mount, and `MainForm.DriveQuotaFor` substitutes them in our own drive
list. Matched on the *letter*, never on the drive type: a subst drive reports
itself as Fixed, exactly like the disk underneath it.

**The orphan sweep has to ask each letter what it points at.** It used to try
exactly one combination — the *preferred* letter and the *canonical* root name —
and both halves are wrong in precisely the situations that make an orphan.
`DriveLetter.Assign` moves to the next free letter when the preferred one is
taken, which it is whenever an earlier run left it behind; `PickRoot` moves to
`GoogleDrive-2` when the directory is stuck, for the same reason. So a killed
process left a letter the next launch could not see, that launch took a
different letter, and being killed in turn left another — each one showing up as
a fixed disk with the host volume''s label and size, indistinguishable from the
real mount and from each other. `QueryDosDeviceW` over A to Z, removing only
letters whose target is a `GoogleDrive*` directory under our own AppData, is
both simpler and safer than guessing which letter it might have been.

**The letter is `subst`, through `DefineDosDevice`.** A sync root is a directory,
not a volume, so there is no other way to give it one. It belongs to the logon
session, so it is set up on every start rather than installed, and it must be
taken down on the way out.

**Off by default.** This application is the shell handler for every folder on the
machine and starts afresh constantly; signing in to Google on the way in is not
something to do uninvited.
### Video containers, for the sound in them

**`.mp4` and `.mov` are in the audio list on purpose.** A recording of a
conversation is a recording of a conversation whether the camera was running or
not, and for somebody listening rather than watching, handing one to a video
player is handing it to a window with no keyboard route to the volume. Shift+Enter
still opens either in whatever owns the extension, which is how to actually watch
one.

**`.mkv` and `.avi` followed** (settings version 12, which appends only those
two). Windows has a byte-stream handler for each: `mfmkvsrcsnk.dll` for
Matroska, and `mfsrcsnk.dll` for AVI. `VideoContainerTests` writes both by hand:

- **AVI:** PCM, with and without a video stream. Both decoded fully, measured
  with a probe. A chunk id naming a stream that does not exist (`01wb` when
  there is only stream 0) makes Windows refuse the whole file (0xC00D36C4).
- **Matroska:** AAC from Windows' encoder, as ADTS, unwrapped into
  `SimpleBlock`s with an AudioSpecificConfig as `CodecPrivate`.
- **PCM in Matroska is refused** as "the decoder will not produce float"
  (0xC00D36B4). It is rare, and such a file goes to the application that owns
  it, like any other the player cannot open. So does an MKV whose audio Windows
  has no decoder for.
- **Windows' MP3 encoder refused 48kHz stereo PCM** through the sink writer, so
  no MP3-in-AVI fixture exists.

**A real one, measured on 2026-09-17:** an 8.53GB, 56-minute 1080p Matroska
episode on the Drive letter, with DDP 5.1 (E-AC-3) audio.

- **It decodes.** Windows' E-AC-3 decoder works on this ARM64 machine.
  Downloaded to disk, 49 seconds of audio decoded in 0.53s, 93 times real time.
- **Opening it reads the start and then the last 256KB**, where the Cues index
  is: 4s warm, 8.4s cold. The first of those end reads fetches a megabyte
  directly, and the rest come from it. A download opens with a 45-second
  budget, not 15; the 15 failed this file cold.
- **It cannot stream on this link.** It is 2.55MB/s of file. Google gave
  1.6–3.9MB/s that day. Media Foundation's Matroska source also reads ahead:
  64MB had been read by 4.7 seconds of audio. So it plays in short bursts
  between "Buffering". The trace is `StreamingSource.Trace`, off unless a probe
  sets it.
- **A slow read that then failed used to end the track.** `ReadAt` counted its
  twenty seconds of retries from the start of the read, so a read that had
  waited half a minute got none. It now counts from the first failure, with at
  least three attempts.
- **Audio only is not something a Matroska file can be read for.** Its audio
  is thousands of small blocks between video frames, with no index to where
  they are. The Cues index only video keyframes. Finding a block means reading
  the one before it, and one request per block at most of a second each is far
  slower than reading the video. MPEG-4 does index every audio chunk
  (`stco`/`stsz`), so there it could be done; it has not been.
- **Media Foundation's Matroska reader races ahead of the audio.** It had read
  190MB by 8.5 seconds of sound, about 65 seconds of the file. So on a link
  slower than the file's own rate, it is always at the edge of the download.
- **The download is a window now** (2026-09-17, asked for as "keep the entire gb
  filling until the end as the track goes"). See `StreamingSource`:
  - pieces of 4MB, fetched from the first missing piece at the reader's
    position onwards, at most three quarters of the budget ahead;
  - when the budget is full, pieces furthest behind go first, then those
    furthest ahead, so a quarter stays behind for skipping back;
  - a file within the budget is filled in completely, behind as well;
  - the first megabyte is fetched first and alone, and only then do the
    pieces start. A 64KB head, followed by a direct read fighting four piece
    downloads and a 4MB tail piece, took five to six seconds to start a FLAC.
    One megabyte on an otherwise empty link took two to three. The last piece
    is no longer fetched early: a decoder that reads the end, as Matroska
    does, gets it by a direct read, once;
  - a read the window lacks waits 400ms for a piece already coming, then
    fetches up to 1MB itself and keeps it for the reads that follow;
  - workers never give up while the track is open; they back off up to ten
    seconds.

  This is not the reverted Prioritise. Pieces are independent, a skip throws
  nothing away, and no download is torn down. Measured on the episode: 192MB in
  87 seconds from Google directly, playing within 4 seconds, 540MB of
  process at most.
- **A track on the letter downloads straight from Google**
  (`AudioPlayer.RangeSourceFor` → `GoogleDrive.OpenRange` →
  `DriveRangeSource`, four streams). Through the letter, the provider's pool
  held a second copy, and a read of a placeholder can strand a process. The
  cursor's plain read-ahead is skipped for Drive tracks for the same reason.
- **A range read times out on a stall, not on its total.** `ReadAttemptLimit`
  is reset by every block. Fifteen seconds for the whole of 4MB failed every
  piece on a slow link, over and over.
- **A probe ended by its watchdog in the middle of a seek on this file** became
  a stuck process, although the provider had answered. Reads are now
  cancellable: `StreamingSource.Dispose` cancels them, because the handle is
  asynchronous. A probe must still never `Environment.Exit` while one of its
  reads is out.

Nothing in the decoder had to change for them. They are the same MPEG-4 container
`.m4a` has always been — the registry points `.mp4`, `.mov`, `.m4a`, `.m4v` and
`.3gp` at one byte-stream handler, `{271C3902-…}` — and `AudioDecoder` already
deselects every stream, selects only `MF_SOURCE_READER_FIRST_AUDIO_STREAM`, and
reads from that index alone. A video track is never asked for rather than being
skipped past.

**A list that grows reaches nobody unless the version grows with it.** The append
lives behind `SettingsVersion < N`, so widening the existing block would have been
writing a migration that has already run everywhere: every installation is past
version 8. `AudioFiles.LaterExtensions` gained the two names and version 11 got
its own block running the same idempotent append, so a file of any age ends up
with each extension exactly once and a list somebody has pruned keeps its pruning.
The suite loads a real version-10 file and checks it gains them, because that is
the file every existing installation actually has.

**The fixture is written by Media Foundation itself.** There is no `.mp4` in this
repository, and a test that needs somebody to put one there is a test that quietly
stops running — the same argument `OpusDecodeTests` makes for having Concentus
encode its Opus. The sink writer encodes AAC into an MP4, which is the same
encoder a phone or a recorder used to make the files this is about, and the
decode is then the real `TrackDecoder.Open` producing a real 440Hz tone at a real
amplitude.

The `.mov` case is the same bytes under the other name, and that is the test
rather than a shortcut: `.mov` and `.mp4` are the same ISO base media format
pointing at the same handler, so what is in question is only whether the
*extension* reaches it. It does.

**What is argued rather than measured** is the one thing the fixture cannot be:
a file with a video track in it. That would want an H.264 encoder as well. The
stream-selection code above is why it is safe, and it is the part to check first
if an `.mp4` ever plays silence.

### Ogg Vorbis, and a sniff that was never tested

**Every Ogg Vorbis file in the application failed to play, silently, for as long
as the managed decoder has existed.** Reported as ".ogg files don't play", and
the cause was one line of reasoning nothing had ever checked.

`OggDecoder` tries Opus first and used to decide with
`OpusOggReadStream.HasNextPacket`, on the reasoning written down beside it: that
the flag "is false when the container was not Opus at all, which is how this
doubles as the sniff". Measured against Concentus 2.2.2, with a hand-built Ogg
page carrying a *Vorbis* identification header:

```
OpusOggReadStream constructed
HasNextPacket = True
DecodeNextPacket -> null
```

So the Opus reader claimed every Vorbis file, `Open` returned success, the
decoder reported itself as 48kHz stereo, and NVorbis — sitting immediately below,
perfectly able to play the file — was never reached.

**And the failure was worse than silence: it hung.** `DecodeSome` looped
`while (frames < WantFrames && _opus.HasNextPacket)` and did `continue` on a null
packet. A reader handing back null for ever with `HasNextPacket` stuck true is a
loop that never returns, on the pump thread, so the track neither played nor
ended. The suite's watchdog reported it as a ninety-second stall, which is what a
hang looks like from outside.

Two fixes, and they are independent:

- **`Sniff`** reads the first page and asks what the first packet begins with:
  the eight bytes `OpusHead` that RFC 7845 puts at the front of every Opus
  stream, or `0x01 "vorbis"` that the Vorbis specification puts at the front of
  every Vorbis one. `HasNextPacket` is no longer the sniff.
- **The empty-packet loop was bounded** (eight). That reader has since been
  replaced by `OggOpusReader`, whose `Read` returns zero only at the end and
  always reads further into the file while it skips, so the count went with it.
  The rule stands: an unbounded number of empties is a hang whatever the reason.

**And then the fix was wrong in the other direction, which is the part worth
keeping.** The first version read the head with `ReadExactly` and treated a
failure as a verdict. On the Drive letter the stream is a download, and
`StreamingSource.Read` reports *no bytes* for an offset it has not reached yet
rather than blocking — so `ReadExactly` threw, a genuine Opus file was declared
Vorbis, and NVorbis walked a large remote container looking for a Vorbis stream
that was not there until the fifteen-second open budget ran out. "Could not play
… it did not open in time", on a file that had played the day before.

The rule that came out of it is one line: **an unknown is never a no.** So there
are three answers and not two —

| the first packet says | what happens |
| --- | --- |
| `OpusHead` | the Opus reader, and only it |
| `0x01 vorbis` | NVorbis, and only it |
| nothing readable | Opus first then Vorbis, which is the order this always had |

A positive identification is trusted and not second-guessed, because it is the
format's own statement about itself: a stream that says Vorbis and will not open
as Vorbis is a broken file, and passing it on to the Opus reader could only
produce the false success the sniff exists to stop. And a head that will not read
is *waited for* — twenty attempts fifty milliseconds apart, against the fifteen
seconds the whole open is given — because the head of a file is the first thing
any download fetches and the part `AudioPrefetch` has already warmed.

`SlowStreamStillOpensTests` holds that: a real Opus file served through an
`IStream` that answers its first reads with nothing at all, exactly as a cold
download does. It was watched going red against the fault and green against the
fix, and so was the Vorbis one.

**The gap that let it through is the interesting part.** `OpusDecodeTests`
generates a real Opus file with Concentus and decodes it thoroughly — rate,
channels, duration, peak, seeking, turning it round at the end. It is a good
test. It only ever tested **Opus**, and the suite had never decoded Vorbis at
all, because NVorbis is a decoder and there is nothing in the build that can
*write* a Vorbis file to test against. So the format with no encoder available
was the format with no test, and it stayed broken.

`VorbisIsNotOpusTests` fills it without an encoder, by building a real Ogg page —
capture pattern, lacing table, and Ogg's own CRC-32, which is not the reflected
one in `Crc32` that zip and gzip use — carrying the 30-byte Vorbis identification
header. It is not a playable file and does not need to be: what is being asked is
only whether the Opus path *claims* it, and a claim is a decoder handed back for
a file it cannot decode. The test was watched going red against the bug and green
against the fix, which is the rule this file states two sections up.

### Audio CDs

**A `.cda` file is a 44-byte pointer, not audio.** The CD file system shows each
track as `Track01.cda`; the stub holds the track number (offset 22), first
sector as an LBA (28) and length in sectors (32). `CdTrackStream` opens
`\\.\D:` and reads the audio with `IOCTL_CDROM_RAW_READ` (0x2403E, offset in
2048-byte units, `TrackMode` CDDA), which returns 2352-byte sectors that are
already the body of a 44.1kHz 16-bit stereo WAV. So it is the `AiffStream`
trick again: our own WAV header, then the sectors, and Media Foundation does the
rest — rate conversion, exact seeking, the duration. Measured on the USB
MATSHITA UJ8B0 (2026-09-19): 20 sectors in 242ms, no elevation needed.

- Twenty sectors a read, and the last block kept. A read larger than the drive's
  transfer limit fails rather than splitting.
- An unreadable block is retried a sector at a time, and what still fails plays
  as silence. Five seconds of failures in a row is treated as the disc having
  gone, or an ejected disc plays silence to the end of the track.
- **A disc plays through; a folder does not.** `OnTrackFinished` starts the next
  `.cda` on the same disc, only if nothing else has started meanwhile. There is
  a short gap at each change of track, because it is a new open.
- **File > Play CD** finds the first audio disc (on a worker — it spins the
  drive), navigates to it with the cursor on track 1, and plays it. Hidden on a
  machine with no CD drive; `HasOpticalDrive` asks drive types only, so it is
  free while the menu draws.
- `.cda` joined the extension list in settings version 13.
- `CdTrackTests` serves a track from sectors in memory, starting part way into
  the disc, and checks it decodes sample for sample like the WAV at 44.1 and 48kHz.

The USB drive drops off the bus now and then (it vanished for a few minutes the
day this was written); `Win32_CDROMDrive` and `Get-ChildItem D:\` from a
sandboxed shell both fail to see it even when it is there — use
`[IO.DriveInfo]::GetDrives()` and an unsandboxed shell.

## Compressing and extracting

Three commands, on the **context menu** — Shift+F10, or the Apps key:
**Compress…**, **Extract**, and **Extract here**. Compress asks a question and
the other two do not, which is why only one has an ellipsis — Extract makes a
folder named after the archive, uniquely, and puts everything in it, so there is
nothing to decide and nothing it can land on top of. Extract here is the one
that can collide, and it asks whatever `PasteConflict` says to ask, once, about
the top-level names, exactly as a paste does.

**The context menu and not the Tools menu**, which is where these first went and
where they were wrong. A command that acts on the selection belongs one
keystroke from the row it is about; Tools is three keystrokes and a different
mental place, and it is where the things that act on the *folder* live — measure
this folder, announce this folder. They also have no keyboard shortcut now, and
so nothing reserved in `ReservedShortcuts`: a reservation for a key nothing uses
is a key taken away from the audio hotkeys for no reason.

**Extract is only on the menu when the row could be extracted.** This menu is
read out one item at a time, so every entry on it is paid for on the way past
every other entry — the same bar the Type column failed. An Extract that can
only ever answer "that is not an archive" is a word heard on every opening of
the menu to be useful on almost none of them. Compress stays unconditionally,
because anything selected can be compressed.

The decision is made from the **name**, never by looking inside the file.
Deciding what goes on a menu is not allowed to cost a read: the row may be on a
share or on the Drive letter where opening it is a round trip, and this runs on
the UI thread every time somebody presses Shift+F10. The bytes are still
sniffed when Extract is actually chosen, which is the moment somebody is waiting
for an answer rather than a menu waiting to be drawn.

Formats: **zip**, **tar**, **tar.gz/tgz**, **tar.xz/txz**, **tar.bz2**,
**tar.zst**, **7z** and a bare **gz** are all created and opened; **rar**,
**cab**, **iso**, **cpio**, **ar** and **lha** are opened. `ArchiveFormats.All`
is the list and it is the only place any of that is written down.

### Where the codecs are, and why that decides everything

There are three engines, and the split is not aesthetic — it is where the code
that can actually compress lives.

**Zip, tar and gzip are ours**, because .NET has deflate and because those three
are what a selection of files is overwhelmingly compressed into. They are also
the ones worth making fast, and the parallel writers for them are the whole
reason this feature is quick.

**xz, bzip2, zstandard and 7z are Windows'.** `%SystemRoot%\System32\tar.exe`
has been in Windows since 1803; on this machine it is bsdtar 3.8.4 over
libarchive 3.8.4, built with zlib, liblzma, bz2lib and libzstd — which between
them are every compressor this application does not have. Driving it is the same
bargain `RoboCopyEngine` makes with robocopy.exe.

**Which tar is measured, not assumed.** The update of 2026-09-27 moved
System32's `archiveint.dll` to libarchive 3.8.8 (10.0.28000.3086), and that
build crashes (0xC0000005) or says "unreadable filename" on any non-ASCII name.
The 3.8.4 build is still in WinSxS. `BsdTar.Executable` creates and extracts a
non-ASCII .7z with System32's tar. If that fails, it tries a copy of tar.exe
beside each WinSxS `archiveint.dll`, newest first, in
`%LOCALAPPDATA%\ExplorerNative\tar\<version>\`. That works because the
application directory is searched before System32. The choice is saved in
`choice.txt`, keyed on System32's DLL version, so the next Windows update is
measured again and a fixed System32 is used on its own. Writing an LZMA encoder by
hand would be thousands of lines that have to be exactly right or they silently
produce an archive nobody else can read.

**Bare `.xz`, `.bz2` and `.zst` are neither, and are refused.** A single raw
compressed stream is not an archive and libarchive will not open it as one:
`tar -c -f - @thing.xz` answers "Unrecognized archive format", and so does
`tar -x -f thing.xz`. There is no decoder on the machine to reach them with. The
table knows what they are anyway, so the message can say *"a bare .xz holds one
compressed stream with no name in it… a .tar.xz opens normally"* rather than
"not an archive". The gzip one is different because that one is ours.

### The encoding table, which decided the shape of `BsdTar.cs`

**libarchive on Windows loses non-ASCII names wherever it has to render one as
text.** This is the single most important fact in this part of the codebase and
it was found by measurement, not by reading. A file called `日本語 🎵.txt`
survives some routes through bsdtar and not others, and the difference is
whether the name ever passes through the process locale — which on this machine,
and on nearly every Windows machine, is not UTF-8.

| route | name survives |
| --- | --- |
| name as a command-line argument, bsdtar walks the disk itself | **yes** |
| the same name on standard input via `--null -T -` | no |
| our own tar piped in with `@-` and repacked to .7z or .tar.xz | no |
| `tar -c -f - @thing.tar.xz`, converting an archive to a tar | no |
| `tar -tf thing.7z`, listing to standard output | no |
| `tar -x -f thing.7z -C somewhere`, extracting to disk | **yes** |

Two of the six work, and they are the two where the name goes between the
archive and the Windows API as wide characters and is never a string in between.
Those are the only two used. To create, the names are command-line arguments and
bsdtar enumerates the folders itself. To extract, bsdtar writes the files to
disk itself.

**Four designs were tried and thrown away, and they are listed so they are not
tried again.** Feeding our own PAX tar in through `@-` — clean, keeps every
decision in tested code, and mangles the names. `--options hdrcharset=UTF-8` on
either end — accepted, and changes nothing. `--null -T -` with UTF-8 bytes on
stdin — the paths are not found at all. Reading names from `tar -tf` for the
listing — right for ASCII, quietly wrong for everybody else, and *the place it
would have been wrong is the collision check*, so it would have extracted over a
file it had decided was not there. That last one is why nothing here lists the
bsdtar formats — extraction stages them and reads the names off the disk — and why
`BsdTar.Totals` reads sizes out of `tar -tvf` with a regular expression that
deliberately stops before the name.

**And bsdtar's zip writer cannot store a non-ASCII name at all.** It converts
through the code page and does not set the UTF-8 flag, so a zip *it* makes from
`日本語 🎵.txt` contains `___ __.txt`. Nothing here writes zips through it — ours
sets bit 11 and stores UTF-8 — but the suite's "an archive nothing here wrote"
check uses a `.tar.gz` for the full comparison and scopes the zip half to ASCII,
with a note saying why. That is a limit of the other tool, not something to fix
on this side.

### The staging folder

Extraction through bsdtar goes into `.extracting-<8 hex>` **inside the
destination**, and then the files are moved into place under this application's
own conflict rules. Two reasons, and only one of them is the encoding.

The other is that bsdtar overwrites whatever is in its way without asking, and
Skip, Rename and Fill gaps are answers this file manager gives everywhere else.
Staging is how those survive. It costs nothing: the staging folder is inside the
destination *deliberately*, so both ends are on one volume and every move is a
rename rather than a copy — the difference for a forty gigabyte archive is a
second against ten minutes and forty gigabytes of writes that did not need to
happen.

The consequence is that the conflict question is asked *after* the decompression
rather than before it, which is the one way this differs from the zip and tar
paths. It reads as backwards and is better: every name is known and correct by
then, nothing already on disk has been touched, and answering Cancel costs the
decompression rather than leaving a folder that is neither the old contents nor
the new.

### How the parallelism works, and what it is actually worth

A zip is not one compressed stream: it is a run of independently compressed
entries followed by a directory saying where each starts, and nothing in an
entry refers to any other entry. So entries are deflated by as many workers as
there are cores while one thread writes them out in the order the directory will
claim — and extraction parallelises for the mirror-image reason, because the
directory gives every offset and N handles on one read-only file are N
independent readers.

A gzipped tar has no per-entry boundary, so `ParallelGZip` cuts the *stream*
instead. RFC 1952: "A gzip file consists of a series of members." Each 2MB chunk
becomes a member of its own, compressed by whichever worker is free, written out
in order. This is what pigz does. The cost is that a chunk cannot refer back into
the one before it, so the first 32KB of each — the length of deflate's window —
compresses without history: **measured, 0.09% larger** than the single stream.
The benefit is that a gzipped tar is fast even when the archive is one enormous
file, which is the case a zip cannot answer.

Measured, 142MB corpus (72 files, half compressible text and half incompressible
binary), Balanced:

| | time | rate | notes |
| --- | --- | --- | --- |
| **zip, ours, 10 workers** | **3.3s** | **43 MB/s** | |
| zip, ours, 1 worker | 14.1s | 10 MB/s | |
| zip, `ZipFile.CreateFromDirectory` | 14.3s | 10 MB/s | |
| zip, system tar | 12.4s | 12 MB/s | |
| **tar.gz, ours, 10 workers** | **3.6s** | **40 MB/s** | 0.09% bigger than one stream |
| tar.gz, ours, 1 worker | 13.8s | 10 MB/s | |
| tar.gz, system tar | 12.6s | 11 MB/s | one deflate stream |
| **extract zip, 10 workers** | **0.24s** | **301 MB/s** | |
| extract zip, 1 worker | 0.95s | 76 MB/s | |
| extract zip, system tar | 0.90s | 81 MB/s | |

**Four times, not ten, and the missing six are not in this code.** This machine
reports ten processors and will not run more than about 3.8 threads at once: a
burn loop of pure arithmetic on ten dedicated threads measures 3.8 cores of
processor time per second, which is the same figure the compressor gets. Scaling
inside the pipeline is clean right up to that point — one worker 1.0 cores, two
2.0, four 3.7 — and flat after it. That is what running out of machine looks
like rather than running out of design, and it is worth knowing before anybody
spends a day trying to find the other six cores.

The three levels, same corpus, zipped: Fastest 1.8s / 78MB, Balanced 3.6s /
72MB, Smallest 8.9s / 72MB. **Fastest is five times quicker and 8.4% bigger; the
extra five seconds Smallest costs buys a tenth of a percent.** Three levels and
not nine, for the reason `SettingChoices` exists: nobody can say what six means.

### Two things that were wrong and are worth recognising again

**The bar sat at nothing and then said done.** Reported as "the progress bar on
making a compressed file just sits there", and every test in this suite passed
through it: the round trips pass a null progress sink, and the totals at the
*end* were always right. Each entry small enough to compress in memory was
counted only as the writer picked it up, so the deflating — which is where the
time goes — was watched by nothing. A 44MB zip reported three times: zero, zero,
finished. `CompressToMemory` now advances the count as it reads, and the write
counts zero so nothing is added twice.

**And a directory entry must not take the name.** A folder is a header and
nothing else, written the instant the pipeline starts — over the top of the name
of the file a worker has just begun compressing, which then stood for the whole
job. Compressing one track named the folder above it from beginning to end. The
worker names its file as it starts compressing it; the writer names only what it
is actually writing, and never a directory.

The test for it compresses **one** file, and that is the whole of why it works.
With a folder of six the old code still moved partway, one step per entry
written, so a check for "any report between nothing and finished" passed against
the bug — measured at 0%, 91%, 100%.


**A worker count that only bounded the queue.** `threads` controlled the
capacity of the `BlockingCollection`, and the compression itself went to the
thread pool, which runs as many as it likes. Asking for one worker and asking
for sixteen produced *the same wall clock*, and every test still passed, because
the output is byte-identical either way. It was found by measuring CPU time
against wall time — 1.0 cores means one, whatever the parameter says — and the
fix is a semaphore taken by the producer.

**And releasing that semaphore at the wrong end is the same bug wearing a hat.**
In the zip writer the lane is given back when the entry is *written*; in the
gzip writer it is given back when the member is *compressed*. The difference
matters and it is not symmetry for its own sake: the zip writer's byte budget is
what bounds memory there, so holding a lane until the write keeps finished
entries from piling up — but doing that in the gzip writer, where the emitter is
one thread writing to one file, would let compressed chunks sit holding a
worker's place while they waited their turn, and the number actually compressing
would collapse towards the speed of the writer.

### Things the format makes you get right

**Zip slip.** `ArchiveEngine.SafeTarget` resolves the combined path and then
checks the result is genuinely under the destination, rather than searching the
name for `..`. Searching for the dangerous string is how this is usually got
wrong. Three separate refusals, all tested: a parent-relative name, an absolute
or drive-relative one (`C:evil.txt` reaches another drive's current directory),
and a UNC one — and that last check has to happen **before** the leading slashes
are trimmed, because `\\server\share\evil.txt` becomes `//server/share/…` and
trimming first turns it into the innocent-looking `server/share/evil.txt`. A
*single* leading slash is dropped rather than refused, which is what every tar
implementation does with one.

Names Windows cannot store — a colon, a trailing dot, `CON` — are refused with
the name in the message rather than silently renamed or silently dropped. A
silent rename is a file somebody later cannot find; a silent drop is a file they
do not know is missing.

**Empty folders.** Directories are entries in the plan in their own right, not
implied by the files inside them. This is the same hole `FileOperations` had:
a plan that is only files loses every empty folder without a word. An archive is
worse than a copy for it, because the evidence is inside a file nobody will open
until later.

**Already-compressed entries are stored, not deflated.** Deflating a jpeg spends
a core to make the file 0.1% bigger. The decision is a list of extensions, not
an entropy test, because sampling costs a read before the decision and gets the
answer wrong on exactly the cases that matter — a WAV of silence looks
incompressible in its first 64KB and an installer looks compressible in its
header.

**Zip64 only when something needs it.** It is legal to write the zip64 records
always, and writing them always is how an eleven-file archive ends up rejected
by whatever the recipient happens to unpack it with. The central directory's
zip64 extra field carries only the fields that actually overflowed, in a fixed
order.

**A file too big to buffer is deflated by the writer as it streams**, with the
header patched afterwards — which is why the output has to be seekable. The
alternative the format offers, a data descriptor after the data, is read
correctly by everything modern and confuses enough older tools that two seeks
are worth it. This is also the honest limit of zip: an archive that is one
enormous file is single-threaded here and in every other zip tool, because
deflate is defined as a single stream of back-references. A gzipped tar has no
such limit, which is why the chooser offers it second.

**The gzip trailer belongs to the last member.** Anything that reads the size of
a .gz without decompressing it — `gzip -l` — reports the final chunk. Equally
true of anything pigz produces and of two .gz files joined with `copy`, and not
true of anything that actually decompresses. The suite asserts it, so that it is
recorded as known rather than discovered later and "fixed".

**CRC-32 is an ARM instruction here.** ARMv8 defines two families and the first
one, `Crc32`, is the IEEE 802.3 polynomial — the one zip and gzip use. `Crc32C`
is Castagnoli, a different checksum, and using it would silently produce archives
nothing could verify. Measured over 256MB: 134 MB/s a byte at a time, 300 MB/s
from the slicing-by-eight tables, **2.0 GB/s** from the instruction. What makes
that worth having is not the deflate, which is thirty times slower than any of
the three — it is the *stored* entry, where the only work is a checksum and a
copy, and at 300 MB/s the checksum was what held back a disk measured at 2.3
GB/s.

### Testing it

A writer and a reader written by the same hand agree with each other about
whatever they both get wrong, so a round trip proves the two halves are
consistent and proves nothing about whether the file is a zip. Every format
written by the suite is therefore also handed to bsdtar — a different
implementation, by different people — and the zips are additionally opened with
`ZipArchive`, which verifies every entry's checksum as it reads it and is what
anybody else's .NET would use.

The corpus is chosen for the ways an archiver goes wrong: an empty folder, a
zero-byte file, a name that is not in English, a file whose extension says it is
already compressed, a file too large to buffer, and two sources from different
folders with the same name. One check is worth keeping in mind — **one worker
and sixteen must produce the same archive, byte for byte**. That is the property
that says the ordering in the pipeline is real rather than accidental, and it is
what makes an archive reproducible.

## What an app-wide review found

Five reviewers read every source file at once, one area each, and everything
below was verified against the code before it was changed. The pattern worth
keeping is what they mostly found: not arithmetic, but **promises the code makes
and then does not keep** — a thing announced that did not happen, a guard that
was never given back, a name that was right for the wrong file.

**A counter taken in one method and given back in another is not a guard.** The
provider's in-flight count — the thing that stops the process becoming
unkillable — was incremented in the placeholder callback and decremented in the
helper it calls, which two of its exit paths never reach. It survived a green
suite for an afternoon. The decrement belongs in a `finally` on the method that
took it, and a test now reads both callbacks and checks exactly that.

**Settings written from outside the running copy are lost unless it is told.**
`--install` wrote SetAsDefaultFileExplorer true, asked the running instance to
stand down, and that instance saved its own older settings over the top on the
way out — so the copy the installer then started read false and deregistered
every folder on the machine, seconds after reporting success. `--uninstall` was
the same shape, and reinstalled itself on the next launch. Both send
`ReloadSettingsCommand` now, which every other command-line flag already did.
The rule this file already stated was right; two callers simply did not follow it.

**A dialog that closes with OK has to do what OK does.** "Connect Google Drive"
set `DialogResult.OK` and closed without running the applies list, so every edit
made anywhere in Preferences was discarded while the application announced
"Preferences saved". Its neighbour had the mirror fault: "Sign out of Google"
set the field behind the check box instead of the box, and OK — which applies
from the box — put it straight back, so signing out left Drive switched on.

**Replacing a file means writing beside it and swapping, never opening it.**
Every archive engine opens its output `FileMode.Create`, so answering "Replace
it?" and then cancelling had already destroyed the old archive — and the cleanup
deliberately kept the wreckage, on the grounds that a file that was there before
belongs to somebody else. It builds `name.zip.creating-xxxxxxxx` alongside and
moves it into place on success.

**Cancelling has to take its own half-written files with it.** The managed copy
engine — the one that handles collisions by renaming — left `song (2).flac`
part-written at the destination with no cleanup anywhere, under a name nothing
flags. The robocopy path has removed its own since the 20GB incident.

**A unique name has to be unique against what is arriving, not only against the
disk.** Extracting an archive holding both `a` and `a (2)` into a folder that
already has `a` renamed the arriving `a` to `a (2)` — the archive's own — and
wrote both trees into one folder, files inside them overwriting each other, and
reported success.

**A timeout on the whole of a job is not a stall detector.** The system tar was
killed after thirty seconds of wall clock, so any 7z or tar.bz2 big enough to
take a minute was killed mid-write, its partial archive deleted, and the user
told it "did not finish". It is judged by whether the process is still spending
processor time now, with the cancellation token as the other way out.

**One unreadable file must not cost the whole archive.** The zip writer's
streamed path — anything over 64MB, and anything already compressed — threw, and
nothing caught it, so a completed multi-gigabyte job was deleted on the way out.
Small files had always been collected as errors and skipped.

**A cancellation has to survive being wrapped.** Disposing the parallel gzip
writer finishes the last member, which takes a lane, which throws on an
already-cancelled token — and that was wrapped as `IOException("Compression
failed")`, so a deliberate Escape came back as an error rather than "Compressing
cancelled".

**Every transient failure that deletes something is a bug.** A 500 from Google's
token endpoint deleted the saved refresh token, costing a browser sign-in for a
server error that was over by the next request; only `invalid_grant` means the
token is really dead. And `WaitOutNetwork` answered "true" even when it gave up,
which both callers read as "it came back" and un-spent the attempt — a single
file retrying for ever, two hours at a time.

**A partial answer served as a whole one is worse than a failure.** A short read
from the cache was handed to Windows with a success status, leaving the required
range unsatisfied and the reader waiting for bytes nobody would send until the OS
timed the whole read out. And a `200` to a ranged request — a server ignoring
`Range` — was read as though it were the range asked for, filing the head of the
file under the middle of it.

**The audio path had three of its own.** The pump's working buffers were sized
once from the device's channel count and a device switch changed that count
underneath them, which is an unhandled exception on a background thread and the
process exiting, not going quiet. Two renderers overlap during a switch and both
were taking frames from one ring. And pausing with a fade does not pause — it
leaves the pause to the last tick — while every volume key cancels the fade, so
play/pause followed by volume-up inside the fade announced "Paused" and played
on. Mute during a fade was undone the same way.

**And the small ones, each of which had the same shape.** The output-device
dialog showed "Follow the system default" for a device that had been unplugged
and returned the unplugged device's id. The drive list never read the row it
landed on, so Backspace out of `C:\` was silent. Ctrl+L into a folder that
cannot be read left the keyboard on the form. New folder, New file, the compress
dialog and the properties sheet all did filesystem work on the UI thread. The
startup registry entry could name a path inside `bin\`. `Settings.Save` staged
through one `.tmp` name shared by every process. A launch arriving while the
application was quitting was accepted by a copy that could no longer act on it,
so the folder opened nothing.

## A count is a word, and the plural is part of it

Four places said **"1 items"**, and every one of them is read aloud: the status
bar on every folder with one thing in it, the tab announcement on every switch,
"Selected 1 items", and a Drive folder reporting itself loaded. This is an
application where the argument for removing a *column* is that it costs a word on
every arrow press, so a wrong word on every folder is not a nicety.

`NameRules.Items` is the rule — "1 item", "7 items", "0 items". It is deliberately
**not** `SayCount`, which drops the number at one because "Copied" already says
everything; here the number *is* the message, so one has to be spelled out rather
than implied. Both are in `NameRules` because both are pure and both are the sort
of one-liner that gets retyped slightly wrong at each site, which is exactly how
these four drifted apart in the first place.

The status bar also carries "and there are more" for a set of search results that
stopped at the cap. `Pane.SearchTruncated` was being written and never read —
the same shape as `DriveFileCache.FetchedAhead`, which this file already records
as worse than not measuring — and the announcement it duplicated is gone the
moment anything else speaks. "10000 items" to somebody who cannot see the list
run off the end is a straight untruth.

## Source-shape tests pin decisions, not punctuation

Two of them broke in one session for changes that *fixed* bugs: one pinned
`await client.FindByName(term, token)` and lost to adding `ConfigureAwait`, the
other pinned a one-line `if` and lost to wrapping it in a try/catch. A test that
fails because a guard was added is a test teaching the next person to delete the
guard.

These tests exist because MainForm needs a message loop and is not compiled into
the suite, so they are the only way to hold a rule the compiler cannot see — but
what they should match is the smallest thing that *is* the decision.
`if (Active.SearchTerm is { } term)` is the decision; the braces after it are not.
Where the shape genuinely matters, count it instead: the async-void guard is
asserted as *every* `await RunSearchAsync(` being inside
`try { await RunSearchAsync(term); }` — it was "exactly two" until a third
caller arrived with its guard and failed the test for having one.

## Traps specific to Windows

**A bare drive letter is not the drive.** `C:` means *the current directory on
C:*, so `Path.GetFullPath("C:")` returns wherever the process was started. This
bites in two directions:

- Incoming: the registered command is `"app.exe" "%1"`, and for a drive the shell
  substitutes `D:\`, giving `"app.exe" "D:\"`. Windows reads the backslash as
  escaping the quote, so the argument actually delivered is `D:"`. Every path
  arriving from outside goes through `NameRules.NormaliseLaunchPath`.
- Outgoing: the same shape breaks a robocopy command line, which is what
  `RoboCopyEngine.QuoteDir` is for. A root is written `D:\.`.

**The Windows context menu costs milliseconds *per selected item*.** Building it
is `SHParseDisplayName` plus `SHBindToParent` for every path, and those are not
cheap: measured on this machine at **4.4ms each**, consistently, warm or cold. So
three thousand selected files is thirteen seconds of the window not responding
before anything appears, and a real folder is very much worse. There was no limit
at all.

`ShellContextMenu.BuildBudgetMilliseconds` bounds it, and it is a *time* budget
rather than a count because the per-item cost belongs to the machine and to
whatever shell extensions are installed — a number of items that is instant here
is a freeze somewhere else. Over budget, the whole menu is abandoned and
`shell.menu.slow` says so, pointing at the application's own commands, which have
no such limit. Never a menu built from the first N items: a Windows *Delete* that
quietly applied to two hundred of your twenty thousand is a far worse bug than
the freeze it replaced.

Two things to know before working on this. It is not one keystroke away —
Shift+F10 and the Apps key show the application's *own* menu, and the shell one
is behind Tools → "Windows context menu" and the context menu's "More Windows
options…". And `Show` takes the budget as an optional parameter purely so a test
can pass zero: with the real two seconds, whether the refusal happens depends on
how fast this machine's shell is, and a test that builds the menu instead is a
test that puts a context menu on screen and waits for somebody to dismiss it,
because `TrackPopupMenuEx` does not return until they do.

**A retried clipboard write has to rewind what it is handing over.** The
"Preferred DropEffect" that makes a copy a *cut* is a four-byte `MemoryStream`,
and `Clipboard.SetDataObject` reads it from wherever its position happens to be.
The retry loop — which is there because another application holding the clipboard
briefly is normal — handed over four bytes on the first attempt and nothing on
the second. An empty DropEffect is not "no opinion", it is the absence of the
flag: Ctrl+X, Ctrl+V, and the file is still where it was, having announced "Cut 3
items". `stream.Position = 0` at the top of every attempt.

**`Path.Combine(dir, typedName)` treats the second argument as a path.** A typed
`..\..\thing` walks out of the folder. Validate with `NameRules.DescribeBadName`
before combining.

**`DelegateExecute` beats the command string.** When claiming a shell verb, an
empty `DelegateExecute` must be written alongside the command, or the machine-wide
handler wins and the registration silently does nothing.

**Robocopy overwrites unconditionally.** The conflict policy is applied *before*
it runs: colliding items are split off to `FileOperations` (which can rename) or
skipped. Never hand a collision to robocopy.

The managed engine is no gentler — `CopyFileAsync` opens its destination with
`FileMode.Create`, which truncates. So for both engines the *only* thing
protecting a file the user already had is that the plan pointed somewhere else.
`ConflictSafetyTests` asserts that for both engines and all three policies, and
it asserts on **content**: a count says a collision was noticed, only reading the
bytes back says the file survived it.

**Robocopy skips a file whose size and last-write time already match**, whatever
the bytes say. That is its same-file optimisation and it is worth keeping, but it
means Overwrite is not a guarantee of a byte copy — and it makes a naive test
flaky, because two three-byte fixtures written in the same second are "identical"
to robocopy about half the time. Give conflict fixtures different lengths and an
older destination, or test something else.

**A cancelled transfer must not leave something that looks finished.** Robocopy
is killed where it stands and it *preallocates*, so cancelling a 20GB copy five
seconds in left a 20GB file at the destination: the first six gigabytes were the
real thing and the remaining fourteen were zeros. Same name as the source, same
size as the source, sitting in the folder — nothing to look at would tell you,
short of reading it. The worst case is somebody deleting the original because the
copy obviously worked.

`RemoveHalfWritten` runs on every cancelled exit, and it is deliberately narrow:
only files under a destination that **did not exist** before the transfer
(`newRoots`), because once a destination already existed there is no telling our
half-written bytes from somebody else's file. It also keeps anything whose source
is *gone* — that is a move which already finished that file, and deleting the
destination would destroy the only copy there is.

**Deciding what "finished" means took four wrong answers, so do not re-derive
it.** Not *"robocopy printed the name"*: its stdout is block-buffered through the
redirected pipe, so the line for a file it has only started arrives when the
buffer flushes — which killing it does — and every half-written file therefore
announced itself as complete. Not *the size*: preallocation means a file that is
one percent written already has its final length, which is the whole reason the
bug is invisible. Not *the timestamp*: robocopy stamps the source's last-write
time on as it closes the file, but an unclosed one carries the time it was
created, and that matches the source's whenever somebody copies a file they have
just made — which a test fixture does every time. Not **the tail** alone either, the
fourth wrong answer. Robocopy does not always write front to back: a file
killed part way was found with its last 64KB written and the two megabytes
before them still zeros. That is what the intermittent "nothing incomplete was
left behind" failure was.

`LooksComplete` compares a file up to 64MB whole. For a bigger file it compares
the tail and blocks at doubling distances back from the end. An unwritten
stretch runs from wherever writing stopped towards the end, so one of those
blocks lands in it at any size, for a few dozen reads.

- An all-zero tail is refused only for sampled files. A file compared whole is
  proven either way.
- A file whose date matches a source older than the transfer is taken as
  finished without reading it. That is the timestamp answer again, with the
  coincidence it had ruled out. Without it, a late cancel read back
  everything already copied.

**The name on the progress window comes from the destination, not from
robocopy.** `/MT` is always passed, even for one thread, and in that mode
robocopy prints a file's line when the file *finishes*. The bytes had already
been moved off its output (see `BytesLanded`); the name had not, so copying one
track showed "(preparing)" from start to end — out of Drive too, whenever the
track was already on disk and the provider's own name never fired.
`WatchLandings` watches the destination for files being created, which robocopy
does under their final names as it starts each one, and the item count takes
"started less threads" as a floor. Two versions of the test passed with the fix
taken out before the one that did not: it copies a *single* file and requires the
name in the first half of the copy.

**And the cleanup goes on a worker.** It deletes a part-written 20GB file and may
wait out a handle that has not been released, on a continuation that resumes on
the UI thread — measured at 294ms of the window not answering, at the exact
moment somebody has just asked for something to stop. `await Task.Run(...)` took
it back to 12ms.

**"Ask" has to be resolved by the window, and for a long time it was resolved by
nobody.** `PasteConflictPolicy` has four members and one of them did nothing.
`RoboCopyEngine` carried the comment *"AutoRename (and Ask, which the caller
resolves before us)"* — describing a contract no caller implemented — and its own
branch read anything that was not `Overwrite` or `Skip` as auto-rename, while
`MainForm` handed `_settings.PasteConflict` straight through. So somebody who
picked "Ask" in order to be warned before a file was replaced was never warned,
was never told why, and got "keep both" every time. Neither engine can ask
anybody anything — `RoboCopyEngine` has no UI and the only production call to
`FileOperations.RunAsync` passes a null callback — so the resolving has to happen
in the window, and `MainForm.ResolveConflictPolicy` is where.

Three things about it:

- **Asked once for the whole paste, never once per file.** Two hundred collisions
  is two hundred modal dialogs, each read out in full, and the second opens
  inside the first one's modal loop — which this file already records measuring
  go wrong with *two*. `CollidingNames` works out what clashes first and
  `ConflictForm` names it.
- **Asked about top-level names only**, because that is what both engines decide
  on: robocopy splits colliding *sources* off before it runs, and the managed
  engine resolves one destination per source and copies the tree under it. A
  question about anything finer would be a question about a decision nothing
  downstream makes.
- **Nothing to decide means no dialog.** A paste that would not have overwritten
  anything is not a paste worth interrupting.

`ConflictForm` is a drop-down of sentences rather than a row of buttons, for the
reason every other list here is one, and Escape means the whole paste is off. A
test reads `MainForm.cs` and checks the raw setting never reaches an engine,
because the failure has no symptom: everything works, it simply works as though a
different option had been chosen.

**An engine that resolves a conflict has to say which file it resolved.** Both
engines applied the policy correctly and reported nothing but a count of files
copied, so a paste that renamed your track to `song (2).flac` or replaced it
outright said "Copy complete" and left you to find out later. `ConflictOutcomes`
comes back on `TransferResult` and `FileOpResult` with counts *and* names — the
new name for a rename, because "Saved as song (2).flac" is the whole value of
the message and a number is not. The name lists are capped at 32; past that the
count is the story. It is also built at every exit, cancellation included: a
paste stopped at the fourth collision has still skipped three, and reporting
nothing there makes a partly-done operation look like one that never started.

**An existence check has three answers, not two.** `DirectoryExistsAsync`
collapses "timed out" and "returned false" into one bool, which is right for a
caller that only wants somewhere to go and wrong for anything that has to
explain itself: a NAS that is asleep and a folder that has been deleted want
different sentences, and saying "not available" for the first sends somebody
looking for a file they have not lost. `ProbeDirectoryAsync` returns
`Present`/`Missing`/`NotResponding`, and `DirectoryExistsAsync` is one line on
top of it. Never wait on the probe task again after the timeout wins — reading
its `Result` there reintroduces exactly the block the timeout exists to escape.

**Resolve the name collision before checking for recursion.** Pasting a folder
back where it lives is how you duplicate one, and the desired destination is then
the source itself. Checking `IsWithin` first reads that as copying a folder into
itself and refuses — so "copy, paste" on a folder did nothing at all. Auto-rename
turns the destination into `name (2)` first, which is not inside the source.
Overwrite still resolves to the source and is still refused, correctly.

**A copy is a shape, not just a list of files.** `BuildPlan` walks directories as
well as files, because a folder that is empty (or whose whole subtree is empty)
otherwise vanishes with no error and no failed count. Robocopy keeps them —
that is what `/E` means — so without this the same paste kept or lost folders
depending on whether it happened to hit a name collision.

**A registered shell command is not a command line.** `%1` is frequently
registered unquoted, because the shell quotes what it substitutes and we do not:
an unquoted `%1` becomes several arguments the moment a path contains a space.
`ParseRegisteredCommand` normalises every `%1` to `"%1"`. It also refuses
templates containing `%I`, `%D`, `%2`… — substitutions only the shell can make
(`%I` is a handle to memory in the calling process, not text) — and hands those
back to `ShellExecute` rather than launching something nonsensical.

**`WaitForExitAsync` does not drain redirected pipes.** Follow it with the
synchronous `WaitForExit()`, or the last lines of output are lost — which showed
up as a copy stalling near the end.

## Default file explorer

`ShellRegistration.SetDefaultFileExplorer(true)` claims the `open` verb on
`Directory` and `Drive`, plus the File Explorer CLSID's `opennewwindow` (Win+E).

**Never claim Win+E either.** The File Explorer CLSID's `opennewwindow` verb has
no command line at all — Windows implements it *in process*, with only a
`DelegateExecute` pointing at a COM object inside the shell:

```
HKLM …\{52205fd8-…}\shell\opennewwindow\command
  (default)       = (empty)
  DelegateExecute = {52205fd8-5dfb-447d-801a-d0b52f2e83e1}
```

Taking it over therefore means blanking that `DelegateExecute`, and the shell
uses the same verb for far more than the keyboard shortcut. With it blanked,
navigation that expected to be handled in process launched this application
instead: opening Settings and moving to the Power page closed Settings and put a
file manager on screen. There is no safe version — Win+E belongs to File
Explorer, and the application's own global hotkey is the way to summon this
window. `ReleaseWindowsEShortcut` sweeps the old claim off on every launch.

**Never add `Folder` to `OpenVerbClasses`.** It reads like the general case and
is not: every shell folder inherits from it — Control Panel, Settings, This PC,
Recycle Bin, Network, and every applet inside them — so claiming its open verb
does not mean "all folders", it means "all of Windows". It was claimed once, and
opening a Control Panel item or a Settings page launched this application with
something that was not a path at all. `Directory` and `Drive` define their own
open verb, which takes precedence over the inherited one, so nothing on disk is
lost by leaving `Folder` alone. `ReleaseOverreachingFolderClass` runs on every
launch to sweep the old claim off machines that ran that build — unconditionally,
because someone who simply switched the takeover off would otherwise be left with
Control Panel still broken. Everything is written under `HKCU\Software\Classes`,
which the shell merges *over* the machine-wide definitions — Windows' own
handlers are never touched, so removal restores stock behaviour rather than
reconstructing it. Pre-existing HKCU values are copied to
`HKCU\Software\ExplorerNative\ShellBackup` first and put back on removal.

The desktop, taskbar and Start menu stay with `explorer.exe`. Those are the shell
itself, replaceable only through the Winlogon `Shell` value, which leaves a
machine with no desktop if the replacement does not implement one. Do not go
there.

### One executable

`PublishSingleFile`, **not** self-contained. A publish is one 2MB file holding
our code, the decoders and the native NVDA client. The .NET 8 Desktop Runtime
comes from `C:\Program Files\dotnet`. See "Why the executable is 2MB now" for
the trade. Not trimmed, because trimming and WinForms disagree about reflection.

Two things follow from bundling:

- **Native libraries are not beside the executable.** The host unpacks them to a
  directory of its own; `NativeLibrary.TryLoad` with a bare name does not look
  there. `Speech.LoadNvda` reads `AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES")`
  and probes it. Without that the app runs perfectly and silently cannot speak.
- **`AppInstall.Install` prunes.** Copying alone only adds, so installing the
  single-file build over the older multi-file one left its DLLs and config
  sitting beside the new executable.

### The registration never names a build output

This is the rule that makes rebuilding safe. Shell registration writes an
absolute path into the registry, and that path has to keep working — so it names
an *installed* copy in `%LOCALAPPDATA%\Programs\ExplorerNative`, never anything
under `bin\`. Rebuild, republish, `dotnet clean`, delete `bin\` entirely: folders
keep opening, because nothing in the build tree is what the registry points at.

```powershell
ExplorerNative.exe --install        # copy this build to the install dir + register folders
ExplorerNative.exe --install-only   # copy it, leave the association alone
ExplorerNative.exe --uninstall      # deregister and remove the installed copy
```

`--install` copies, registers, and starts the copy it just installed. It does
*not* ask a running instance to stand down first any more — see "Why it no longer
waits" above; the old binary is renamed aside instead, and a live instance is
asked to restart afterwards as a courtesy rather than as a precondition. Ticking
the Preferences box installs too, via
`TrayApplicationContext.EnsureInstalledIfTakingOverFolders`, so the UI route can
never register a build path either.

Three rules in `ShellRegistration.ReconcileFolderOpen` keep it that way:

- A copy that is **not** the installed one never rewrites the registration. This
  is what stops running the debug build from silently re-pointing every folder on
  the machine at `bin\Debug` — which it used to, and the next rebuild then broke
  them.
- A registration pointing at an executable that **no longer exists** is repaired
  on the next launch, whoever launches. Folders opening nothing is worse than the
  rule.
- Otherwise the path is refreshed only when the running copy is the installed one.

Recovery, if the app is ever unreachable while holding the association:

```powershell
ExplorerNative.exe --unregister-default   # hand folders back to File Explorer
ExplorerNative.exe --unregister-all       # remove every registry entry
```

These run before the single-instance lock and exit without a UI, on purpose.

They also send `SingleInstance.ReloadSettingsCommand` to any live instance. That
is not a nicety: the running copy holds the settings it loaded at startup and
writes them back when it closes, so without the message it silently overwrites
the change — and then reconciles the registry against the stale value on the next
launch, removing the registration the user had just asked for. Anything else that
edits settings.json from outside the running process needs the same treatment.

`ShellRegistration.Reconcile` returns whether it changed `RegisteredExePath`, and
that return value must be saved. Dropping it makes every launch believe the
executable has moved and rewrite the shell entries again.

Deregistering only ever removes a value that is *ours*. With no backup recorded —
which is the state straight after a previous deregistration — the old code
deleted whatever was there, so a stray `--unregister-default` wiped whichever
application actually owned folders.

**The listener''s read needs a deadline, and it must be shorter than the connect
timeout.** Reading the request was a blocking `StreamReader.ReadLine`, so one
client that connected and never wrote — killed between the two calls, or simply
not us — parked the listener for ever, and from that moment every folder
double-click on the machine opened nothing. Nothing recovered it but a restart
and nothing reported it. Only one server instance exists at a time, so a real
launch arriving during such a stall sits in `Connect`: keeping
`ReadTimeoutMs` (2s) below `ConnectTimeoutMs` (2.5s) is what makes the stall
clear first so that launch still gets in. Raise it above and a misbehaving
client goes back to costing a folder rather than a delay. The bytes are also
decoded once at the end rather than per chunk — a path can carry a character
whose UTF-8 runs across a read boundary, and per-chunk decoding turns it into
replacement characters.

Once registered, **every folder double-click starts a new process**. It must hand
its path to the running instance and exit — see `SingleInstance` and
`TrayApplicationContext.HandleExternalRequest`. A launch carrying a folder also
overrides "start minimised", or the double-click puts nothing on screen.

## Configuration must survive a rebuild

Settings live in `%APPDATA%\ExplorerNative\settings.json` — never beside the
executable, so nothing in the build or install tree can touch them. `--install`
loads, edits and saves, so it preserves everything it does not set; and it asks a
running instance to exit first, so that instance's own save-on-exit cannot land
afterwards and overwrite the change.

`SettingsRoundTripTests` walks every persisted property by reflection, sets each
to a non-default value, saves, reloads and compares. A setting added later is
covered without anyone remembering to update the test. It also checks that a file
written by a *newer* build still loads, so a downgrade does not wipe a
configuration it does not fully understand.

Verified end to end: mark the config with distinctive values, `dotnet clean`,
delete `bin` and `obj`, publish, `--install` — settings and folder registration
both intact.

**A string property declared `string` can still arrive null.** The deserialiser
has no opinion about nullable annotations, so `"AudioExtensions": null` in
settings.json produces null in a property every consumer was written against as
never-null. Most of them survive it by luck; `Migrate` does not — it calls
`AudioExtensions.TrimEnd`, throws, and `Load` catches everything and hands back a
default `Settings`. **One null in the file therefore discarded every preference
in it**, and the next save wrote the defaults over the original. Nothing was
reported, because from outside it is only an application that came up with its
settings reset.

`Settings.NoNullStrings` runs *before* `Migrate` and puts every non-nullable
string back to empty. It is reflection over the properties rather than a list of
names, for the same reason `SettingsRoundTripTests` is: a string setting added
next year is covered without anybody remembering. The annotation is the rule — a
property genuinely declared `string?`, like `InitialFolderOverride`, is meant to
be null and is left alone.

**Never point tests at the real settings file.** `Settings.OverrideAppDataDir`
exists for this. The suite used to back up and restore the user's own
`settings.json`, which races with the running application's save-on-exit and did
in fact change a live preference.

And there is a second way to break that rule, which is subtler and which a test
sweeping the settings by reflection walks straight into. `GetProperties()` with
no `BindingFlags` returns the **statics** as well, and `SetValue` on a static
ignores the instance it was handed and sets the static — so a sweep that assigns
a hostile value to every property assigns one to `OverrideAppDataDir`, which is
the hook redirecting the suite away from the real file. It points the tests back
at the user's own configuration and then writes to it. `PersistedProperties()`
asks for `Public | Instance` and is the only way these sweeps enumerate.

## An update never changes a setting

The rule since 2.0.0, at Conner's request: an update never reverts, changes or
alters a setting the file already has. A new setting arrives at its default
simply because the file does not mention it. `Settings.Migrate` therefore
forces, resets, rewrites and appends nothing; the old steps that moved shortcut
defaults, switched announcements off or added formats to the list are gone.
`Validated` may only repair a value that is genuinely broken (missing, blank,
outside any range a version ever allowed, or not a valid choice), and a range is
never narrowed. `UpdatesNeverChangeSettingsTests` loads a file with every setting
set, stamped as versions 0, 5, 9, 12 and current, and requires every value back
unchanged.

New installs have no global shortcuts: every audio shortcut defaults to empty
and the show-window hotkey to unassigned, until somebody sets them in
Preferences. A file that has them keeps them.

## Dead code is a build warning

`.editorconfig` turns the unused-member diagnostics up to warnings, so it cannot
accumulate again: IDE0051, IDE0052, CS0169, CS0414, CS0649, CS0162, CS0219. No
formatting or naming rules are enabled — only the ones answering "is this still
used", which the compiler answers better than a person reading.

Two things it does **not** catch, so sweep for them by hand after removing a
feature:

- **Public and internal members.** IDE0051 only sees private ones. Extract the
  declared names and count references across `src` and `tests`; anything
  appearing exactly once is a candidate.
- **Unnecessary usings and stale XML comments.** IDE0005 only runs with
  `GenerateDocumentationFile`, which also turns on the missing-doc warnings, so
  it is an on-demand command rather than a permanent setting — the recipe is in
  `.editorconfig`. It is worth running: it found two doc comments referring to a
  `Trace` that had been deleted, and one `<summary>` describing `ProgressBytes`
  that had drifted onto the method below it, documenting neither.

**Interop fields always look dead and must never be removed.** A struct passed
to Windows is a memory layout, and a field nothing reads is still holding an
offset — `WAVEFORMATEX.BlockAlign`, `CF_CALLBACK_INFO.CorrelationVector`,
`CMINVOKECOMMANDINFOEX.hInstApp`, `IO_COUNTERS.ReadOperationCount`,
`PropVariant.Value1`. The same goes for interface members that exist to satisfy
a contract: `StreamingSource` implements `IStream`, so `SetSize`, `Revert`,
`LockRegion` and `UnlockRegion` stay whether or not anything calls them.
`CfApi.CheckLayout` would catch a struct mistake; nothing would catch the
interface one until Windows called the wrong slot.

**What the sweep found worth keeping as a lesson:** a counter maintained on
every read and never once looked at (`DriveFileCache.FetchedAhead`), which is
worse than not measuring; a `WndProc` override that did nothing but call `base`,
kept only to hold a comment; and two settings that had stopped meaning anything
— `AudioLowLatencyStart` and `AudioDirectOutput`. That last pair is the one to
watch for. **A tick box that cannot change what happens is worse than no tick
box**, because somebody will turn it off to test a theory and draw the wrong
conclusion when nothing changes. When a feature goes, its setting goes with it.

## Testing notes

`tests/Tests.cs` compiles the real production sources listed in
`SelfTest.csproj`, not copies. Adding a new non-UI source file means adding it
there too.

- Assert on behaviour, not on this machine. A test that asserted `.flac` opens
  with foobar2000 failed on any computer configured differently, which said
  nothing about whether the code worked.
- `ShellRegistrationTests` really does write to the registry. It snapshots the
  affected keys itself — independently of the code under test — and restores them
  in a `finally`, then asserts the machine was left as found. Keep that shape.
- `PermissionTests` uses real protected locations (`System32`) rather than
  synthetic ACLs, because that is what a user actually walks into.
- **Never truncate the suite''s output with `Select-Object -First N`.** PowerShell
  stops the upstream pipeline when the count is reached, which kills `dotnet run`
  mid-suite and leaves a `SelfTest.exe` holding the build outputs — so the next
  build blocks on the locked binary and the run looks like a hang in the tests.
  Collect the whole run, or redirect it to a file and read the tail.
- **Do not run the suite through `dotnet run` with an `OutputPath` override.**
  `dotnet build -p:OutputPath=obj\x\` writes there, but `dotnet run` launches
  the RID-specific `bin\Debug\...` copy regardless — so it happily executes a
  binary from an hour ago. That cost a round of chasing a test failure that had
  already been fixed: the source scan reads `src` at run time and found the new
  ids, while the stale catalogue compiled into the binary had not been told they
  were live. The overrides exist so a *running app* does not block a build;
  nothing holds the test binary, so run it plainly.

### Ask NVDA what it said — do not infer it

Focus landing on the right row does **not** mean the row was spoken, and no
amount of checking focus position or watching WinEvents will tell you. NVDA logs
every utterance:

```powershell
& "C:\Program Files\NVDA\nvda.exe" -r --debug-logging   # temporary, config untouched
# exercise the app, then read %TEMP%\nvda.log for lines containing "Speaking ["
& "C:\Program Files\NVDA\nvda.exe" -r                   # restore normal logging
```

**`GetWindowText` reads nothing useful out of another process's controls.** It
does not send `WM_GETTEXT` across a process boundary; it returns the window's
cached title, which for a child control is whatever was there when the handle was
made. Reading the progress dialog's seven fields that way returned "Starting" and
"calculating" for the entire sixteen seconds of a 20GB copy, at 100ms sampling,
twice — and the conclusion drawn from it was that the progress window was frozen
and shipping a bug. It was updating perfectly the whole time.

What settled it was `EXPLORERNATIVE_TRANSFERLOG`, which showed `Update` running
on the UI thread every 250ms with the byte count climbing. What *proved* it was a
screenshot: `CopyFromScreen` over the whole virtual screen, then look at the
picture. Do that early — it is one command and it cannot be misread. Capture the
whole screen rather than the window rect, because a DPI-unaware harness gets
virtualised coordinates back from `GetWindowRect` (a 560x340 dialog measures
373x227 at 150%) and copies the wrong region.

**UIA is not a reliable oracle for "is this dialog on screen".** A `MessageBox`
put up by this application was visible, focused, and read out in full by NVDA,
while `AutomationElement.RootElement.FindAll(Children, TrueCondition)` returned
seven top-level windows and none of them was it — twice, minutes apart, from a
separate process. Concluding from that the dialog had not appeared is a whole
round of chasing code that was working. Use `FindWindowW("#32770", title)`, which
found it immediately and by handle, or enumerate with `EnumWindows` and read the
class name. And when what is being checked is whether a *person* would notice
something, the log below is the answer, not the window tree.

This is the only reliable oracle, and it settled a question three wrong theories
had failed to. What it showed:

| action | focus event | NVDA speaks |
| --- | --- | --- |
| Arrow keys | yes | yes |
| Enter into a folder | yes | yes |
| **Going up** (Backspace, Alt+Up) | **none** | **no** |

Going up raises no focus event at all — NVDA's log records the keystroke and
nothing else — so the row landed on is never announced. That one case is filled
by `SpeakRow`, gated on `wentUp`. Speaking anywhere else talks straight over
NVDA, which is exactly what an earlier attempt did.

Two dead ends, recorded so they are not retried:

- **MSAA WinEvents are not the whole story.** `EVENT_OBJECT_FOCUS` fires
  correctly for Enter *and* Backspace — hooking it shows both — but NVDA reads
  this control through UIA (`...SysListViewItemListItemUIA`) and ignores them.
- **Declining the UIA provider makes it worse.** Answering `WM_GETOBJECT` with
  zero for `UiaRootObjectId` does not fall back to MSAA; it removes the list from
  the screen reader entirely and *every* key goes silent, arrows included. See
  the comment in `FileListView`.

The log will also show a real UIA fault under load:
`_get_value ... COMError (-2147418113, 'Catastrophic failure')` thrown out of
WinForms' virtual-mode provider, which aborts `speakObject` mid-announcement. It
is intermittent and not currently worked around.

### Driving the keyboard

Type-ahead and position restore can only really be proven against the running
app. Focus it with `SetForegroundWindow`, send keys with
`[System.Windows.Forms.SendKeys]::SendWait`, and read the result through the
list's `SelectionPattern`:

```powershell
$sp  = $list.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
$sel = $sp.Current.GetSelection()   # $sel[0].Current.Name is the focused row
```

Keep the gap between characters well under `TypeAheadBuffer.TimeoutMilliseconds`
(250ms by default, so aim for ~120ms) or each letter starts a new word — which is
the intended behaviour, and the other half of what to test.

Three harness traps, each of which produced a convincing false result:

- `SetForegroundWindow` is refused when the calling process is not itself the
  foreground one, and the keystrokes then go to whatever *is* — a "broken"
  feature that was really keys landing in a browser. Attach to the foreground
  thread with `AttachThreadInput` first (see the `Focus` helper used in testing).
- `Start-Process -Wait` on the app hangs: with no instance running the process
  you launched *is* the app, and `--install` additionally spawns the installed
  copy, which PowerShell then waits on too.
- UIA element references go stale. Re-query after anything closes rather than
  re-testing an old handle, or a dialog that did close looks like one that did
  not. Do not name a PowerShell helper `Where` either — it shadows `Where-Object`.
- **Never force a disabled window to the foreground.** With a modal dialog up the
  owner window is disabled, and driving `SetForegroundWindow` at it anyway and
  then sending keys opened a *second* Preferences dialog stacked on the first —
  which looked exactly like a modality bug and is not one. A person cannot do it:
  clicking a disabled owner flashes the dialog instead. Focus the dialog by its
  own handle, and when a harness produces a state the UI cannot, suspect the
  harness. Enumerate the process's windows and check `IsWindowEnabled` first.

**Reading a dialog's contents from outside works, unlike finding it.** Once
`EnumWindows` has the handle, `EnumChildWindows` plus `GetWindowTextW` reads
every label in the Preferences pages perfectly — because those labels have their
text set at construction, which is exactly what the cached title holds. That is
the other half of the `GetWindowText` rule: it lies about text that *changed*
after the handle was made (a status bar, a progress field) and is reliable for
text that never did. For anything that updates, use UIA on the main window,
which is dependable there even though it cannot see the dialogs at all.

**And shutting the application down for an edit has a supported route.** Send
`<exit>` (`SingleInstance.ExitCommand`) down the named pipe
`ExplorerNative_ipc_<username>` and it saves its state and goes, which is what
`--install` uses. Killing it skips the save and the Drive unmount; editing
settings.json under a running instance loses to its save-on-exit.

The name gains `_s<session>` in any logon session above 1, and the pipe
admits only the current user (`PipeOptions.CurrentUserOnly`).

```powershell
$c = New-Object System.IO.Pipes.NamedPipeClientStream(".", "ExplorerNative_ipc_$($env:USERNAME.ToLower())", "Out")
$c.Connect(2500); $w = New-Object System.IO.StreamWriter($c); $w.WriteLine("<exit>"); $w.Flush()
```

**Never round-trip a source file through `Get-Content`/`Set-Content` to do a
replace.** Windows PowerShell 5.1 reads a BOM-less UTF-8 file as ANSI, so every
em dash in the file comes back as mojibake and is written out double-encoded —
84 lines of `AudioPlayer.cs` in one command, in a repository with no version
control to undo it. It is recoverable (read as UTF-8, `GetBytes` through code
page 1252, decode as UTF-8, write UTF-8 without BOM) but the fix is not to do it:
use the editing tools, which preserve encoding.

## What the second review changed

A whole-application pass after the seeking work. Each of these was verified
against the code, fixed, and — where a test could hold it — tested.

**Settings.**
- A file that will not parse is no longer replaced by defaults. A value of the
  wrong type is dropped alone (`Settings.Salvage` reads the file property by
  property), trailing commas and comments are allowed, and a damaged or
  unreadable file is copied to `settings.json.damaged-*` / `.unreadable-*`
  before anything can overwrite it.
- A file written by a newer build keeps its version stamp and its unknown
  settings (`UnknownSettings`, `[JsonExtensionData]`), so going back a version
  and forward again neither re-runs migrations nor loses settings. From this or
  an older build, unknown names are removed settings and are dropped.
- **OK in Preferences applies only what the dialog changed**
  (`Settings.WithChanges`). It handed back the copy taken when the dialog
  opened, so a volume key pressed while it was up was undone by OK.
  **A reload from outside does the same** (`Settings.ReloadOnto`), against what
  this process last read or wrote: `--register-default` used to reset the live
  volume, speed and window position to whatever was on disk.
- A save survives a reader holding the file: a POSIX-semantics rename
  (`FileRenameInfoEx`) first, then the ordinary move, retried, and the
  temporary file is always removed. Load reads with delete sharing so that
  works. **Windows matches `settings.json.*` against `settings.json` itself** —
  a test that deleted "the copies" with that pattern deleted the file.
- The hotkey page no longer turns a key it does not list into A on OK, and
  refuses a combination with no modifier.

**Install and registration.**
- `ReplaceFile` always stages beside the target and renames over it; the plain
  copy over a live executable could leave half an executable registered for
  every folder on the machine.
- `Install` copies the application's files only (`FilesToInstall`): a
  single-file build is its executable, anything else is the executable plus
  libraries and `ExplorerNative.*`. It copied every file beside the executable
  — for a copy run from Downloads, all of Downloads.
- `--register-default` installs first if nothing is installed, rather than
  registering a build output.
- Taking folders back from an application that took them from us refreshes the
  registration backup, so standing down gives folders to *that* application.
  The refresh skips values that are still our own (`"open"`, a blank
  `DelegateExecute`), or standing down leaves them behind.
- The single-instance pipe is per session and current-user only; a client
  falls back to the old name for a copy started before the change.

**The tray.** Every post to the UI thread goes through `PostUi`, which does
nothing once `Quit` has begun: posting afterwards threw on the worker or ran
against a disposed icon and window. The device dialog saves what OK chose,
which differs from the player's device exactly when the saved one is unplugged.

**Google Drive.**
- **Reconnecting while mounted renews the sign-in in place** (`RenewSignIn`).
  It used to mount from scratch, and choosing a root found the live mount's own
  directory, unregistered it and stood a second provider over it. Mounts are
  serialised (`_mountGate`), and `Unmount` cancels one still on its way up —
  decided under `_gate` on both sides so one always sees the other.
- **`CF_CONNECT_FLAGS` were wrong**: REQUIRE_FULL_FILE_PATH was declared as 2,
  which is REQUIRE_PROCESS_INFO. Checked against `cfapi.h` (win32metadata's
  recompiled copy): 2, 4, 8. `CF_POPULATION_POLICY_PRIMARY` is 0, 2, 3, not
  0, 1, 2 — which may be all that "FULL refuses CfCreatePlaceholders" ever
  measured. `SyncRootConnectTests` stands a provider up over a scratch folder
  to prove the declared flags are accepted.
- A folder-population callback marks its folder fully populated
  (`DISABLE_ON_DEMAND_POPULATION` on the transfer) and in `_populated`, so
  neither Windows nor the window asks Drive for it again.
- Ranged reads have a per-attempt clock covering the body (a stalled body
  waited for ever), retry `IOException`, and refuse a short body instead of
  returning it as the end of the file.
- The access token and its expiry are one immutable value; a 401 forgets the
  token and retries once. A server error from the token endpoint is a network
  failure, not "sign-in expired", and a caller that timed out behind a refresh
  or a consent gets a timeout rather than the sign-in message.
- `IdOrRelist` is the one relisting path for trash, rename and link, on a
  worker: an async method runs on its caller's thread until its first await,
  so the Drive listing ran on the UI thread. `Populate` has the callback's
  45-second deadline.
- An upload into a folder nobody had opened now merges into an existing folder
  of that name (the parent is populated first). `PlaceOne` undoes its map
  entries and tries the next name when "already exists" means something
  unlisted holds the name.
- **Copying a folder into itself recursed** — the copy found itself among the
  things to copy. Contents are listed before the copy is made, and nothing the
  copy made is copied.
- A resumable upload stops on a refusal that is a state (a full account),
  restarts an expired session (404/410), asks Google's count again on the next
  pass when the question itself failed, and waits between attempts
  (`UploadRefusedException`).
- A drive letter is free only when `GetLogicalDrives`, `QueryDosDevice` and the
  remembered network mappings all say so. `Directory.Exists` called an empty
  card reader, a disc drive and a disconnected mapping free, and the alias hid
  the device.

**Copying.** A cancelled transfer now finds what to clean up from what landed
in the destinations it created, not from the planning walk — which stops on
cancel, and a cancel early enough lands before it starts, leaving a
half-written file the flaky "nothing incomplete was left behind" check caught.

**The window.** Navigation is per pane with a generation number, so a slow one
finishing late gives way; the folder watcher's reload is quiet and keeps the
cursor where it is now, and does not run while a search is gathering or the
window is behind another; an operation's refresh goes to the folder it
happened in; Delete lands on the next surviving row; a folder arriving during
startup is queued; Properties runs on an STA thread; Shift+arrow ranges update
the status; a type-ahead jump warms the track like arrowing does.

## Shared drives

The top of the Drive letter has a **Shared drives** folder beside My Drive's
own, as drive.google.com shows it. Drive has no such folder: shared drives are
a separate list (`drives.list`), so the folder is virtual —
`DriveClient.SharedDrivesId` — and each drive inside it is a folder whose id is
the drive's own (a shared drive's top folder has the drive's id).

Three things make it work, and each fails silently without the others:

- **Every request carries `supportsAllDrives=true`** (`DriveClient.AllDrives`,
  applied in `Request`). Without it Drive answers 404 for anything in a shared
  drive — reads, trash, rename — as though the file did not exist.
- **A listing inside a shared drive names the drive** (`corpora=drive`,
  `driveId`). The mount remembers which drive each folder is in
  (`_driveIds`, filled from the `driveId` field of each listing, inherited by
  anything `PlaceOne` adds).
- **Nothing is made inside the list itself**, and a drive is not trashed,
  renamed or moved from here: `RealFolder` refuses the virtual id as a parent,
  `Request` refuses it in a URL, and `GoogleDrive.IsDriveItself` guards the
  commands with a sentence saying where that is done instead.

**Search never spans all drives at once.** Measured on this account, which can
see 64 shared drives: every folder across all of them was more than 50,000
rows and 158 seconds, against 2.2 seconds for My Drive's 1348. So a search is
scoped to the drive it starts in (1–3 seconds for a shared drive, every hit
resolving), the Shared drives folder searches the drives' names, and the
letter's root searches My Drive as before. The folder graph is cached per drive.

## What Windows says about the Drive letter

File Explorer, and anything else that asks Windows, reports the **host disk's**
size for the Drive letter — about 500 GB on this machine, against the
account's 5 TB. The letter is `subst` onto a directory, and a subst letter is
not a volume, so every free-space query is answered by the volume holding the
directory. Only a real filesystem driver could change that, which is what this
design avoids. This application's own drive list shows Google's figures.

The drive list reads each row as **letter, space, free space** —
"C: 431 GB free" — with "of 475 GB" in the size column and the label and kind
of drive in the type column (`MainForm.DriveRowName`). The name is a display
override, so the entry's own name stays the letter for type-ahead and sorting.

## The third review

Three read-only reviewers over the areas the first two passes covered least.
What was real and is fixed:

- **Copying kept "now" as every copy's date** (`FileOperations.CopyFileAsync`):
  the time was set by path while the handle was open, and the buffered tail
  written at close reset it. It is flushed, then set through the handle — and
  the flush comes before "complete", or a full disk at close left a short file
  that the cleanup had been told to keep. The engine's filesystem work after
  planning no longer comes back to the UI thread.
- `NameRules.UniqueAmong` numbered `.gitignore` as `" (2).gitignore"`.
- **The Windows context menu is refused unless every selected item resolves**;
  skipping one built a menu for the rest.
- **One failed read ended a track's download for good** (`StreamingSource`):
  reads are retried, a download that gives up keeps what it had, and `Resume`
  carries on from there. The fill stream has no buffer of its own (a 4MB buffer
  turned the 64KB first read into a 4MB one), and the wait for a download that
  is already coming covers the whole read, not its first byte.
- A download readied for the row the cursor was on is dropped when the cursor
  settles on something that is not a track, and an older, slower `Prepare`
  cannot replace a newer one.
- The audio dialogs cannot stack (a second, then Escape on the first, undid
  the second's saved values), and do not hold the player's messages while open.
  Silence reduction ignores a number typed and then cancelled, and asks the
  speech switch before speaking.
- **Drive:** a second caller of `Populate` waits for the listing in progress
  (sibling folders in one upload made duplicates); a destination that will not
  list fails its files under Skip, Fill gaps and Overwrite rather than
  duplicating them; a name is checked and reserved in one step; Overwrite
  never replaces a folder with a file (it trashed the folder); a cache being
  read is never evicted; a losing duplicate cache does not drop the kept one's
  chunks; a refresh keeps each name bound to the file it named and replaces the
  listing rather than merging it; the root's refresh keeps **Shared drives**
  (it used to give that name to a real folder, which Delete then trashed); a
  full account on the resumable path stops the upload once; connection changes
  are announced as they are now, once each.

## A folder fills in as it arrives

Opening a folder no longer waits for the whole listing. The worker offers what
it has found in batches (`EnumerateSorted`'s `offer`, every 500 entries or
150ms; a Drive folder a page at a time) and `MainForm.ProgressiveLoad` puts it
on screen, sorted, every 300ms. Measured on a 2170-entry Drive folder: the
first 200 rows at 0.4–0.6 seconds, where the folder used to appear whole after
three to five.

Three rules keep it usable with a screen reader, and a probe (the real
`MainForm`, a slow fake source, no window shown) checked each:

- **Nothing is shown for the first 250ms.** A folder that lists in that time —
  every local folder measured, 120,000 files included — appears in one go
  exactly as before.
- **The cursor stays on its item** as rows arrive above it; it is moved with
  the item rather than left on whatever slid into its place.
- **One landing.** The row to come back to (the folder you stepped out of) may
  not have arrived yet; until it does no row is chosen, and the cursor is put
  on it the moment it does — unless somebody has already moved, in which case
  their choice stands. A remembered row that never arrives lands on the first,
  when the listing is whole.

**On Drive**, `DriveClient.ListChildren` hands each page to `onPage`, and
`DriveMount.Populate(path, onPlaced)` makes that page's placeholders and hands
the placed names on before asking for the next page (`PlacementSession` keeps
names unique across pages and keeps every name the last listing bound to a
file). The first page of a watched listing is **200 rows**, because Drive
takes about 1.5 seconds over a thousand sorted rows and 0.4 over two hundred,
with the whole listing no slower. Measured on the real account through a
temporary sync root in the scratch folder, not the letter.

`UseBackgroundEnumeration` is gone: listing is always on a worker, and the
setting had no control in Preferences.

## The dead-code sweep

Everything nothing calls was removed: 380 lines of source, and the tests that
only exercised them. The compiler's own check (the recipe in `.editorconfig`)
only sees *private* members, so the public and internal ones were found with a
throwaway Roslyn tool that compiled `src` and `tests` together, resolved every
reference, and listed each declaration in `src` that nothing in `src` refers to —
then removed them and ran again until nothing new turned up, because removing
one member often leaves the next one unused.

What went, and the three that are worth knowing about:

- **`Notifier`**, a whole class the application never used — the tray routes
  messages itself. Its test now checks the catalogue's routing decisions
  (`Notifications.ChannelsFor`) directly.
- **`FolderSizeMode.Never`**, an option in Preferences that behaved exactly
  like On demand. The enum keeps its numbers (`OnDemand = 1`, `Automatic = 2`)
  because they are what settings files hold, and a saved 0 becomes On demand.
- **`FolderPositionMemory.Forget`**, which nothing called and which was the only
  reason `Remember` needed its dead-name draining.
- The rest: `ArchiveEngine.ListAsync`, `FolderSizeCalculator.CancelAll` and
  `ClearCache`, several player properties nothing read (`CurrentDeviceName`,
  `PeakPercent`, `ClippedPercent`, `Buffered`, `SilenceRemovedSeconds`), unused
  Cloud Files enum values, an unused P/Invoke, unused parameters and usings,
  and a cursor calculation in `LoadEntriesAsync` that was computed and
  discarded.

**Kept on purpose, though only the tests use them**, because each is how the
suite observes something real: `CfApi.CheckLayout`, `AudioPlayer.SelfCheck`,
`ShellLink.TargetOfAsync`, `FileLauncher.ResolveHandlerPath` and
`DescribeCommandLine`, `FetchWatch.Detached`, `DriveCachePool.Used`,
`FolderPositionMemory.Count`, `SilenceReduction.RemovedFrames`,
`WasapiPlayback.ReadPeak` and `SwitchProblem`, `ReservedShortcuts.Combinations`,
`ShellRegistration.IsStartupRegistered`. Helpers that only existed for the
tests' convenience (`TypeAhead.Find` over a list, the Audio menu mnemonic
helpers, `Crc32.Compute`) moved into the tests.

**None of this changes the size of the executable in any way that matters.**
The application's own code is about 0.6 MB; the file is 2 MB now that the
.NET runtime is no longer bundled (see "Why the executable is 2MB now").

## Odd cases

Four reviewers went looking for inputs that are legal and unusual. What they
found and what was done, most of it now in `OddCaseTests`:

- **Delete to the Recycle Bin could delete permanently without asking.** The VB
  helper asks the shell for no confirmation, and without confirmation the shell
  does not warn when something cannot be recycled — a share, most USB sticks, a
  file bigger than the bin. `ShellDelete.Recycle` calls the shell itself with
  `FOF_WANTNUKEWARNING`, so Windows asks exactly in that case.
- **Switching output device did nothing, for a whole build** — a flag set by
  `Stop` was never cleared by `Start`. The test now counts `DeviceSwitched`;
  every check it had passed for a player that ignored the switch.
- **Two selected items with one name** (easy from search results) were pasted
  onto each other, and a move deleted both originals. Both engines now treat a
  name this paste has already given out as taken (`reserved`, `claimed`).
- Archives: a `..` entry reached the rename logic before the refusal; names
  that differ only in capitals overwrote each other in random order (the first
  now wins, the second is reported); a failed bsdtar run moved its half-written
  last entry over a good file; `.txz`/`.tbz2`/`.tzst` could not be replaced;
  7z and .tar.xz stored two entries with one name (now refused, with the way
  round it); read-only files could not be replaced; a 236-character name hit the
  temporary name's length; files dated before 1970 were left out of tars; an
  empty file became a zero-byte, unopenable .gz; cancelling during planning was
  reported as a failure; bsdtar's staging folder was left behind when it held
  read-only files.
- Names: a Drive name like `notes.txt .` kept a trailing space; Drive numbered
  `.env` as ` (2).env` and `Album.2024` as `Album (2).2024`; shortening a long
  Drive name could split an emoji; a name ending in a dot or a space made
  delete and rename act on a different file (`NameRules.LiteralPath`); a copy
  numbered a new file by whatever already had its name; hidden extensions
  blanked dotfiles.
- The window: the folder watcher's quiet reload went through the new
  progressive path and read the row out every 600ms — reloads now wait for the
  whole listing, and the watcher leaves a folder that is still loading alone; a
  late fallback navigation beat a newer one; `C:\Users\` and `C:\Users` were
  different folders; Go to path resolved relative paths against the process and
  expanded `%VAR%` before trying the literal path; an unchanged name with a
  leading space was "renamed"; a not-ready drive landed on A:; a failed search
  left the previous folder's rows under the new folder's name.
- Audio: 5.1 Ogg/Opus came out in the wrong speakers (Vorbis order is not
  Windows order); speed over pitch was capped at four and played 400%/−24
  semitones at normal speed; an Ogg with video in it got a length of a few
  seconds; a one-page Opus file was shifted by its padding; joined MP3s at two
  sample rates reported the first part's length; ring depths were frame counts
  written for 48kHz; an AIFF with a NaN rate got through.
- Startup and settings: a relative folder argument was resolved by the running
  copy against its own directory; a captured key that cannot be written back as
  text is refused; a key pressed during another key's repeat lost its press
  delay; the sign-in page said "Signed in" before checking the redirect; a copy
  of the settings lost the "unreadable on disk" guard; an unset window shortcut
  was reported as taken.

**An Ogg stream whose first page carries a large granule** — a recording
started mid-broadcast — is measured from where it starts. Opus reads the base
off its first audio page. Vorbis can only count a packet with NVorbis's own
delegate, so the base is found on the first seek, which `OpenVorbis` makes on
purpose. The base leaves out the first packet, which produces nothing, and
adds the long-before-short shift that `TrailingShift` describes. Without that
shift the length was 448 samples long at 44.1kHz. Below 96,000 samples a
difference is block rounding, not a late start. The suite shifts the granules
of its two fixtures and compares.

**Intermittent:** "nothing incomplete was left behind" in the cancel test failed
twice in a dozen suite runs and never in thirty probe runs of the same scenario.
The cleanup now retries deletes for three seconds and the test reports which
file was left and how, so the next failure explains itself. It did: two files
matched in their last 64KB and differed from 14MB. The cleanup had trusted the
tail alone, and now checks more (see "Deciding what 'finished' means took four
wrong answers").

## The fourth review

Ten reviewers read the whole application. Every finding was checked against
the code before it was changed. These are the ones worth knowing about.

**Cloud Files structures.**
- `CF_CALLBACK_INFO` had lost its `CorrelationVector`, so the request key was
  read from the process-info slot.
- `CF_OPERATION_INFO` had its fields in the wrong order.
- `CheckLayout` agreed with both, because the expected sizes had been written
  from the same mistake. It is 152 and 48 bytes now. A reviewer claimed 56 for
  the second, which is also wrong: count the fields.

**A placeholder that is already there is not necessarily right.**
- "Already exists" was treated as placed. A file given new contents on the web
  kept its old size, and a copy out of the letter cut the new bytes off at the
  old length.
- `RefreshOne` reads the identity (`CfGetPlaceholderInfo`), size and date, and
  rewrites the placeholder with `CfUpdatePlaceholder` when they differ.
- A handle opened for attributes alone is enough for that call, measured.
- `CF_PLACEHOLDER_CREATE_FLAG_SUPERSEDE` is not a substitute: it took the new
  size and kept the old identity.
- `ReplacedContentsTests` stands a provider up over a scratch folder to hold
  this.
- `ForgetGone` drops map entries for ids a whole listing no longer returns. An
  upload once went into a folder that was already in the Drive trash.

**Drive caches are leased.** `CacheOpenedFor` hands out a cache with a reader
count already taken. Eviction is `TryRetire`, which checks the count and
retires as one step, and removes by instance, never by key.

**A create whose answer was lost is looked for before it is sent again.**
- A timeout, a reset or a 5xx after Google had made the folder, file or copy
  produced a duplicate.
- `SendWithRetry` and `UploadWhole` ask `MadeSince` first: the name, the parent
  and a creation time after the first attempt.
- Failures about the request rather than the moment are not retried at all.
  One is the weekly sign-in expiry, which cost forty seconds of backoff inside a
  folder listing.

**Drive-to-Drive copy and move take the conflict setting** (`OnDrive`):
- skip leaves a clash alone;
- Fill gaps merges folder into folder;
- Overwrite replaces file with file, the new one copied under a free name first;
- a file and a folder are never swapped for each other;
- nothing is copied onto itself;
- a destination that will not list fails every item rather than duplicating it.

Moving out of Drive under Fill gaps keeps the original of anything merged,
because robocopy leaves out what is already there.

**A selection mixing Drive and local items is refused** for move and delete.
Each half used to go down the other half's path.

**Walks skip links, not reparse points** (`NameRules.IsLink`). Cloud
placeholders and deduplicated files are reparse points too. A copy, upload,
archive, search or size of a OneDrive or Drive folder quietly left out what was
in it.

**The player while a track is still opening.**
- Changes made during the open never reached the new player.
- Stop said "Nothing is loaded" and the track started anyway.
- There is an open generation now, and every setting is pushed again when the
  player is installed.
- Resuming respects mute.
- A track that ended short of its length restarts on play.
- The position is never waited for: a read holding the decoder's lock is
  hundreds of milliseconds on the Drive letter, and the position is asked on
  the window's thread.

**"Follow the system default" follows it.** A stream opened on the default
endpoint stays there. Only `ActivateAudioInterfaceAsync` streams are moved by
Windows. So `WasapiRenderer` registers an `IMMNotificationClient` and the
playback switches when the default changes.

**Smaller ones:**
- the Composer tag used the wrong property set;
- surround Ogg on a device with a different channel count is mapped by speaker;
- seeks in a chained Ogg stop at the end of their own recording;
- robocopy's ERROR lines are errors, not copied files;
- "1 seconds" is gone;
- a reload from the command line that finds the settings unreadable keeps what
  is running;
- a command-line reload opens no Google sign-in and shows no shortcut dialog;
- zip extraction keeps the first of two names that differ only in capitals, in
  archive order;
- tar hard links are extracted as copies;
- bsdtar's per-entry errors are reported, and name the entry that failed.

**The fifth and sixth passes** went back over the fixes above.

- **A move out of Drive trashes only what it can show arrived.** `VerifyCopied`
  walks the folder in Drive itself. Every file must have a placeholder here and
  a copy of the same size at the destination. It fails for a Google document, a
  file added on the web since the folder was listed, and a clash deeper down
  that robocopy reports as success. A folder that fails the check stays in
  Drive.
- **A retried create only finds its own work.** Every create carries its own
  `appProperties` tag, and the lookup is by that tag rather than by name and
  time. If Drive will not answer the lookup, the create is not sent again: "not
  found" and "could not ask" are different answers.
- **Uploading a folder follows the conflict setting at the top level**
  (`SettleTopFolders`). Skip leaves the existing folder alone. Keep both makes
  "Folder (2)". A file that already holds the name is a failure.
- **Consent is scoped to one call** (`GoogleAuth.ConsentScope`), not switched on
  for everything. `SignInLapsed` is raised wherever a request finds the sign-in
  gone, not only while a folder is being listed.
- **A placeholder refresh touches only what changed.** It compares against what
  the mount last said about each file and runs on the thread pool. A failed
  update is tried again at the next listing. Pool chunks are filed per cache
  instance, so an old revision's bytes cannot serve a new one.
- **Folders handed over while quitting are relaunched.** They wait in the inbox,
  and a launcher waiting for the lock tries the handoff again every half second.
  A settings reload that arrives while quitting is merged before the last save.
- **A track still opening is called off by Stop and by a newer Enter.** Neither
  can leave its download running.
- **Command-line recovery flags work when settings.json cannot be read.** The
  registry is still handed back; only the save is skipped.

**The seventh and eighth passes** checked those fixes again.

- **Copying inside Drive names each item by what Drive calls it.**
  - `DrivePlaced.DriveName` carries Drive's own name beside the name on disk.
  - `DriveNameOf` reads it from a listing no older than `FreshListing` (30
    seconds). When a paste reaches the first item from a source folder,
    `RelistUnlessFresh` lists that folder again if its listing is older than
    that. It happens once per folder, and Cancel is checked between folders.
    With an old listing, a rename made on the web was quietly undone.
  - Keep-both numbers `RoomForNumber(driveName)`. Numbering a name at the
    length limit hung the window: `Sanitise` cut the number off again, so
    every candidate was taken.
- **Selected items of the same name never replace or skip each other.**
  - `claimed` holds the names this paste has given out.
  - It also holds the names of selected items that already live in the
    destination. That includes a folder a merge walks into: the whole
    selection is passed down the recursion.
  - Items that clash with each other are always kept both, whatever the
    conflict setting.
- **A move out of Drive counts what it kept** (`FinishMoveOutOfDrive`). Skipped
  items, and a check that was stopped, are reported, not trashed.
- **A mis-decoded name from bsdtar is matched as a pattern**
  (`BsdTar.MangledPattern`). Any run of `?` or non-ASCII characters stands for
  one or more non-ASCII characters. Code-page bytes can decode as other, valid
  letters, so a question mark is not the only sign of damage.
- **The single-instance listener waits for a connection without a token.** A
  cancelled wait leaves the pipe listening until it is disposed. A launcher
  that connected in that gap believed its folder was delivered, and nothing
  read it. `StopServer` connects to end the wait, and whoever connected is
  read first.
- **A release made while settings.json was unreadable is used once.**
  - `_pendingRelease` holds one flag per setting a recovery flag switched off.
    `_releasedAt` holds when the last release arrived.
  - The pending flags are applied, saved and cleared when a later reload reads
    the file.
  - The folder release gives way to `Settings.FoldersTakenAt`, which
    `--register-default` and `--install` write. The file's own date was tried
    first and was wrong: `--install-only` rewrites the file without deciding
    anything about folders.
  - Preferences clears only the flag for a setting it switches back on.
  - Left pending, a release handed folders back again at every quit.
- **A change of default is queued, never decided on the spot.** Three rounds
  of a flag (`_defaultMovedDuringSwitch`) each lost a move or undid a pick,
  depending on where a race landed. It is gone.
  - `OnDefaultDeviceChanged` queues `DefaultMoved` unless something is already
    waiting.
  - The pump drops it if a device picked by name is playing by then.
    `SwitchCore` skips it if the default is already what plays.
  - A **named** switch that fails queues `DefaultMoved` itself when the renderer
    it kept follows the default or has been unplugged. The default is asked
    again, not remembered.
  - A failed move to the default is never queued again. That retried as fast as
    a device could refuse, with silence and a spoken error each time.
  - An unplugged device queues `DefaultMoved` too, behind any pick already
    waiting rather than over it.
  - While the renderer playing is lost, the pump queues `DefaultMoved` again
    every 2 seconds, doubling up to 30. Windows says nothing when a default that
    was still connecting becomes usable, and a lost device never reports again.
    Only the first failure is announced.
  - `StartDirect` queues one `DefaultMoved` right after subscribing, because a
    default change during a slow open reached nobody.


## Explorer Native Connect, the web app's server

`ConnectServer` is the HTTP API the web app talks to, on 127.0.0.1 only (see
"The web app" above for the port, Serve and the auth rule). Every request from
anyone but the PC's owner carries the pairing code (`X-Connect-Code`, or `code=`
for media URLs). **The only client is web\app.js; what it calls is the
contract.**

| file | what it owns |
| --- | --- |
| `ConnectServer.cs` | HTTP, routing, validation, the 60 s size cache, the job table, request bodies (Content-Length, chunked, `Expect: 100-continue`), `ConnectStreams` |
| `ConnectFiles.cs` | the real `IConnectFiles`: every file action, done with the app's own machinery |
| `ConnectUploads.cs` | resumable uploads under `%LOCALAPPDATA%\ExplorerNative\connect-uploads\<id>` |
| `ConnectAudio.cs` | `/api/audio`: any playable file as 24-bit WAV through `TrackDecoder` |
| `ConnectClipboard.cs` | the clipboard routes, on their own STA thread |
| `ConnectDetails.cs` | `/api/stat`'s sections: Media Foundation's streams and small container parsers |

Wired in `TrayApplicationContext.StartConnect`. Tests are `tests\ConnectTests.cs`:
the server against a fake `IConnectFiles`, and the real `ConnectFiles` with no
Drive on scratch folders in `%TEMP%` (not delete, which would fill the real
Recycle Bin).

Things that are not obvious:

- **`ConnectFiles` follows MainForm rule for rule, and is a copy of two of its
  methods.** `DriveToDrive`/`OnDrive` and `FinishMoveOutOfDrive` are MainForm's,
  without the window (MainForm's are pinned by source tests, so they were not
  extracted). A change to either behaviour belongs in both. It refuses what the
  window refuses: a move or delete mixing Drive and local items (400), a move
  out of Drive of what the account may not remove (the job fails with 403's
  sentence). Copy into Drive from local goes through `DriveUpload`; out of Drive
  through robocopy with `cloudSource` and one thread.
- **Errors carry their status as `ConnectException`.** Anything else thrown is a
  500 with the message as a sentence. Once a response's headers are out a
  failure is `ResponseStartedException` and only closes the connection; writing
  an error there would be read as body.
- **Delete from the web app never deletes for good.** `ShellDelete.Recycle` with
  its nuke warning would put a question on the PC's screen and park the request
  on it, so `HasRecycleBin` (`SHQueryRecycleBinW`, network and CD refused) is
  asked first and a drive with no bin is a per-item failure.
- **Folder sizes never walk Drive or `%APPDATA%\ExplorerNative`.** A Drive folder
  is `MeasureOnDrive` (folders are counted through its `looking` callback, the
  first call being the folder itself), 60 s budget. `G:\` itself answers the
  quota's used bytes with `complete: false` rather than four minutes of
  listings. A local walk is `FolderSizeCalculator` with a 20 s budget and its
  new `Skip` hook set to our AppData and the Drive letter, because measuring
  `C:\Users` otherwise walks the sync root, which is one Drive listing per folder.
- **Drive's quota is the Drive letter's size** in `/api/drives` (size = limit,
  free = limit - used); no limit is `unlimited: true` with size and free 0 and
  `used` still the account's usage.
- **Drive files are served through `ConnectStreams`**, one `StreamingSource`
  (the player's download window over `GoogleDrive.OpenRange`) per path, leased
  per request and kept 60 s after the last lease, at most three open. A phone
  scrubbing sends a Range per drag and AVPlayer holds two connections to one
  file; opened per request each started its own download. Measured on a 15 MB
  FLAC: cold range 750-810 ms to the first byte, warm 1-16 ms. The first read of
  a response is 64 KB so bytes go out as soon as there are any, and the socket
  is polled five times a second while a body is going out, so a phone that
  hangs up cancels the wait (the read itself finishes into the shared window).
- **`/api/audio` decodes at the file's own rate** by opening `TrackDecoder` with
  rate 0 and channels 0, which both decoders take as "as it is"; more than two
  channels is `Reopen(rate, 2)`, then an average by parity if that did not take.
  A range is mapped to a frame and the decoder seeked to `(frame + 0.25) / rate`,
  so whichever way it rounds it lands on that frame. Verified sample-aligned
  on Opus, Vorbis, AIFF (byte-identical), AAC in MP4 and 5.1 AAC in MKV: the
  lossy ones differ from a straight decode by at most about 1e-5 of full scale
  after a seek (decoder state), and by 0.3% one frame either side. A Drive
  file is decoded from its `ConnectStreams` lease through `RangeStream`, an
  `IStream` with its own position. The header's length is the decoder's
  duration; short is padded with silence, long is cut, unknown is estimated at
  64 kbit/s and never under a minute. RIFF is 32-bit, so past 4 GB it is cut.
- **Resumable uploads**: received is the partial file's length, so a chunk cut
  off mid-body counts for what landed; a chunk at another offset, or while one
  is still arriving, is 409 with `received`. Finish into a local folder moves
  it (`PlaceFileAsync`); into Drive it is a job of kind `upload` whose progress
  is `UploadInto`'s bytes and which carries `path` when done. Ids are 16 hex
  digits and nothing else reaches a path. Swept after 24 h untouched, one level
  deep, on start. Measured: 200 MB with a cut and resume, local and into Drive
  (23 s up to Drive), byte-for-byte.
- **The clipboard is `ConnectClipboard`**, on an STA
  thread of its own with a message-only window registered through
  `AddClipboardFormatListener`; every `WM_CLIPBOARDUPDATE` is a snapshot, a
  `seq` and a wake for its own writes waiting to see them land. Writes are queued to
  that thread and go through `ClipboardInterop` (retries, rewound DropEffect).
  One copy in another program is often several updates, so `seq` can jump by
  more than one. No history is kept, only the current item. Files sent to the clipboard live
  in `%LOCALAPPDATA%\ExplorerNative\connect-clipboard\<seq>-<random>` (or
  `batch-<id>`), swept after 7 days. The suite never touches the real
  clipboard (`FakeClipboard` over the real class's bookkeeping); a live check
  must save it first and put it back.
- **An unread body is drained even on `Connection: close`.** Closing a socket
  with unread bytes is a reset, and the reset can overtake the answer: a POST
  whose route ignored its `{}` body came back to Python as error 10054.
- **`/api/details` (once `/api/stat`) answers the file's basics and sections**, each its own try
  under one 15 s budget (`StatBudget`); what is not done by then goes back with
  `partial: true`, and a section that throws is simply absent. The v2 `tags`
  object is still filled, from the media section when the basics had none.
  - **A Drive file is read through `GoogleDrive.OpenRange`, never its
    placeholder**, wrapped in `CachedBytes` (256 KB blocks, remembered) so the
    parsers' many small reads of a header and a tail cost a few requests. The
    v2 `StatAsync` no longer reads a Drive file's tags through the property
    system for the same reason. A 15 MB FLAC on G: answers in about 0.7 s, an
    MOV from a phone camera in 2-3 s.
  - **Media** is the file's own parsers first (FLAC STREAMINFO, MD5, Vorbis
    comments and PICTURE; Ogg Opus/Vorbis heads and comments, `CHAPTERxxx`
    comments; ID3v2 and the Xing/Info/VBRI and LAME header; MP4 atoms, esds
    profile, iTunes `ilst`, Nero `chpl` and QuickTime chapter tracks, rotation
    from the track matrix; Matroska info, tracks, chapters and tags, found
    through SeekHead past the clusters), then Media Foundation's source reader
    over the same bytes for whatever they did not say. A parser's value wins,
    except an HE-AAC profile, which only the decoder can see (implicit SBR).
    MF's native FLAC type reports no bit depth; the parser's does.
  - **MP4 box names are Latin-1, not ASCII**: iTunes items start with a
    copyright sign, and ASCII turns it into `?`. And **a moov can hold more than
    one `udta`** (Windows' sink writer writes its own), so every one is searched.
  - An `.m4b` whose brand says M4A (what ffmpeg and taggers write) is still
    called an audiobook.
  - **Text** is counted as it streams: a BOM, else one pass checking UTF-8
    validity (ASCII if nothing is above 127, Windows-1252 if not valid), then a
    decode pass counting lines, endings, words and the rest. 50 MB at most.
  - `owner` and `accessed` are local only; `sizeOnDisk` is
    `GetCompressedFileSize`; `kind` is `SHGetFileInfo` with
    `USEFILEATTRIBUTES`, which reads nothing. `driveWebLink` is
    `GoogleDrive.LinkFor(path, makePublic: false)`, which only reads.
- **Drive actions still speak on the PC.** `TrashPath`, `RenameOnDrive` and
  friends announce through `GoogleDrive.Notification` whoever called them, so a
  delete from the web app is heard on the computer too.

Verifying against the live app: HTTP only, from a script, to
`https://laptop.tail3d7403.ts.net/` (through Serve, as the owner) or
`http://127.0.0.1:<port>` with the code — never read `G:` from your own process. Writes on
Drive go in one `zz-connect-test-<random>` folder at the top of My Drive made
through `/api/mkdir` and deleted through `/api/delete`.
