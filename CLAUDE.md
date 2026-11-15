# Explorer Native

Keyboard- and NVDA-first file manager for Windows (ARM64 + x64). WinForms, .NET 10 (`net10.0-windows` since 3.1.0). Two goals settle every argument: be fast, and read well under NVDA. The only packages are Concentus and NVorbis (pure IL, for Ogg/Opus), WebView2 (Apple Music) and SMBLibrary with SMBLibrary.Win32 (pure IL, LGPL, the SMB server, 7.0.0). Add no others. The one bundled program is 7-Zip's 7za.exe (4.2.1, see docs\archives.md), and the one bundled library besides the NVDA client is libopus (opus.dll, 6.1.0, see docs\audio-convert-edit.md).

## Where the rest is (read the doc for the area you touch, before changing it)

This file holds what every change needs. Each feature's rules and history are in docs\ (7.1.0 split, nothing dropped):
- docs\code-shape.md: which file owns what in src\. Read it first when you do not know where something lives.
- docs\settings.md: settings.json, hard-coded choices, migration, export and import. Any change to Settings.
- docs\audio-player.md: player, decoders, seeking, WASAPI, effects chain.
- docs\audio-convert-edit.md: Convert audio, encoders, the audio editor.
- docs\google-drive.md: the WinFsp Drive volume, DriveClient, writes, reads.
- docs\folder-sync.md: the Drive monitor (folder sync) and its 7.0.0 changes.
- docs\web-app.md: Explorer Connect, the web app.
- docs\copy-move-delete.md: copying, moving, deleting.
- docs\archives.md: compressing and extracting, 7-Zip, tar.
- docs\commands.md: --do commands, every action by name.
- docs\apple-music.md: Apple Music browsing and playback in WebView2.
- docs\ai-describer.md: the picture describer.
- docs\favourites-reopen.md: favourite folders, reopening after an update.
- docs\internet-streams.md: radio and HLS.
- docs\windows-traps.md: Windows and shell traps.
- docs\file-servers.md: SMB, FTP, SFTP servers and their 7.0.1 security rules.
- docs\review-rules.md: rules the 5.0.0 sweep, the 6.0.0 review and 6.2.1 added (carrying on after restart, radio, AI credits and more).
- docs\code-health.md: errors.log (Diag), blocking and threading rules, the CI server run, the bug report, timings (7.1.0).

When you add to these notes, add to the doc for that area, not here, unless it is a rule every change must follow.

Rules every change follows, whatever the area:
- When a feature is added or changed, its command comes with it (ExplorerNative --do; a menu item gets one by itself, anything else is registered by hand). Details in docs\commands.md.
- A caught exception that is let go calls `Diag.Note(ex)` (one throttled, encrypted line in errors.log) unless it is truly best effort: cleanup, Dispose, per-row or per-sample loops. Details in docs\code-health.md.
- Nothing blocks the UI thread (no .Result, .Wait(), GetAwaiter().GetResult() or Thread.Sleep there or under a lock), and an async void that is not a UI event handler catches its own exceptions.

## Build and test

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"   # per-user SDK, not on PATH
dotnet build ExplorerNative.csproj
dotnet build tests\SelfTest.csproj
dotnet run --project tests\SelfTest.csproj   # ~1 min, "N passed, M failed", non-zero on failure
```
If the SDK is missing: `Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1; .\dotnet-install.ps1 -Channel 10.0 -Architecture arm64 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"` (`-Architecture x64` on an x64 PC)

- The .NET 10 runtime here is only in the per-user folder: set `$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"` before running SelfTest.exe, or it exits 150 (Program Files has only .NET 8).
- The project defaults to win-arm64. On an x64 machine build and run the suite with `-r win-x64 -o obj\testout` and run `obj\testout\SelfTest.exe` directly.
- The suite is a plain console program (`Tests.Main`, `Check`/`Equal`). It compiles the real sources listed in `tests\SelfTest.csproj`, so add any new non-UI file there.
- MainForm is not in the suite (it needs a message loop). Source-shape tests read MainForm.cs instead. Pin the decision, not the punctuation: a test that breaks when a guard is added teaches people to remove guards.
- A running app locks bin\Release (MSB3027). Build elsewhere with `-p:BaseOutputPath=obj\<name>\ -p:OutputPath=obj\<name>\`, and delete those dirs (~170MB each) afterwards.
- Never pipe the suite into `Select-Object -First N`: it kills the run and leaves SelfTest.exe locking outputs. Never `dotnet run` the suite with an OutputPath override: it runs a stale bin\Debug copy.
- Never edit source through PowerShell Get-Content/Set-Content. PS 5.1 mangles UTF-8.

## Updating the installed copy (a change that is only built is not shipped)

The registry points every folder at `%LOCALAPPDATA%\Programs\ExplorerNative\ExplorerNative.exe`. Finish every change like this. The app does not need to be closed.

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet build ExplorerNative.csproj
dotnet run --project tests\SelfTest.csproj          # green first, always
dotnet publish ExplorerNative.csproj -c Release -o bin\app `
  -p:BaseOutputPath=obj\pubout\ -p:OutputPath=obj\pubout\
$p = Start-Process bin\app\ExplorerNative.exe -ArgumentList '--install-only' `
       -PassThru -NoNewWindow
$null = $p.Handle                   # required, or ExitCode reads empty
$p.WaitForExit(120000)
"exit code $($p.ExitCode)"          # 0 installed, 1 did not
& bin\app\ExplorerNative.exe --read-log | Select-Object -Last 3   # logs are DPAPI-encrypted
```
- Never use `-Wait`. The installer starts the installed copy, which stays running, so `-Wait` hangs.
- Run the staged exe, never the installed one. Install copies from the running exe's own folder, so run from the install dir it does nothing and still reports success.
- Use `--install-only`. `--install` also takes over folder-open for every Directory and Drive and sets SetAsDefaultFileExplorer.
- To roll back, copy `ExplorerNative.exe.retired-<stamp>` (in the install dir) back over the exe. Afterwards, delete the `obj\<name>` dirs.
- `AppInstall.Replace` overwrites if it can. Otherwise it renames the running exe aside (Windows allows a rename, not an overwrite) and renames the new one in. `SweepRetired` runs on install and on launch.
- Never make the install depend on the running instance answering the pipe or exiting.
- Command-line paths never block. The install log is written first. `AttachConsole` runs only when `IsCommandLineRun` (attaching on every launch froze the terminal). Relaunch goes through the shell so it inherits no handles. No MessageBox: the notice auto-closes, and `--quiet` suppresses it. ExitCode is 1 on any failure.
- The new copy starts once the single-instance mutex is free (`StandDown`), not when the path becomes openable.
- Flags: `--install`, `--install-only`, `--uninstall`, `--register-default`, `--unregister-default`, `--unregister-all`, `--read-log <name>`, `--licence`. The recovery flags run before the lock, show no UI, and work when settings.json is unreadable.
- To stop the app cleanly, send `<exit>` down the pipe `ExplorerNative_ipc_<username lowercased>` (plus `_s<session>` above session 1). Killing it skips the save and the Drive unmount.

## Releasing

1. Bump `<Version>` in ExplorerNative.csproj.
2. Add a new top section to CHANGES.txt: the version alone on a line, then one change per line, then a blank line.
3. Push to main first and let `.github/workflows/ci.yml` (build both projects for x64 and arm64, then the server self-test, on every push to main and pull request) go green: a tag on a red commit will not release.
4. Push the tag `vX.Y.Z`. `.github/workflows/release.yml` (7.1.0) runs two jobs side by side: `build` publishes win-arm64 and win-x64 with the tag's version, and `test` runs the x64 self-test with `EXPLORERNATIVE_SELFTEST_SERVER=1`, which must pass. `release` needs both, so a failing test stops the release (before 7.1.0 the test ran after the release with continue-on-error, and a broken build could ship). The server run SKIPs, with the reason on the line, only checks the runner cannot pass (no audio device, elevated, no desktop, shared timing: tests\ServerRun.cs, and only when that is really true of the machine); never add a check there to get a release out. `selftest-full` runs the whole suite after the release for information. The build job also takes that version's CHANGES.txt lines as the notes (the job fails if there are none). Then `gh release create` uploads ExplorerNative-arm64.exe and -x64.exe plus a gzip copy of each (`.exe.gz`, about 40 percent; `Updater` downloads that, checks the unpacked exe against the exe asset's size and digest, and falls back to the exe on any problem). (From 4.2.3 to 7.0.1 the self-test ran after the release because it held every release back ~10 min; since 7.1.0 it runs beside the build, so the wait is the test's length, about 12 to 15 minutes after the tag.) Updater tests: `EXPLORERNATIVE_SELFTEST_ONLY=updates`. Help > Check for updates and Help > Changelog read these releases.
- CHANGES.txt is embedded in the exe (Changelog.cs). Help, Changelog (Shift+F1) reads it offline as one list, Kova's layout: "Version X" headings, Ctrl+Up/Down jumps between them. A test fails if the running version has no section. Only Check for updates goes online, through `GoogleNet.Handler` like everything else; failures name the innermost cause (`GoogleNet.Reason`).
- Conner's style: CHANGES.txt, README and release notes are plain text with no Markdown symbols (no backticks, asterisks or pound signs), because NVDA reads them. Write lines for consumers, not in an AI voice: "New: ...", "Bug fixed: if you did X, Y happened. Now ...".

## The executable

- .NET 10 since 3.1.0 (`net10.0-windows`, both projects; the release workflow sets up 10.0.x; the per-user SDK here is 10.0.402). PublishSingleFile, self-contained (Conner, 3.1.0: nobody installs anything; ~127MB x64), ReadyToRun (not for Concentus/NVorbis), not trimmed (NETSDK1175), not compressed: measured medians 102ms vs 347ms to start, and every folder open starts a messenger. `WFO1000` (designer serialization) is in NoWarn. C# 14: `array.Reverse()` binds to the in-place span Reverse (returns void); write `.AsEnumerable().Reverse()`.
- Native libraries unpack to `%TEMP%\.net\ExplorerNative\<build>`; `AppInstall.SweepOldExtractions` deletes other builds' folders at startup (this PC had 43).
- `Speech.LoadNvda` must find the bundled NVDA client, or the app is silently mute. Self-contained (3.1.0), `NATIVE_DLL_SEARCH_DIRECTORIES` is empty, so a bare name goes through `NativeLibrary.TryLoad(name, assembly, null)`, which knows the unpack folder (measured with a probe build). `Speech.LoadedFrom` records the path; `SweepOldExtractions` uses its folder. Check a release by listing the running process's modules for nvdaControllerClient.
- Every folder open spawns a messenger. `Main` hands the path over the pipe before loading settings, speech or WinForms. Keep slow work off startup (web server, clipboard listener and audio warm run later).

## UI and NVDA rules

- Nothing slow runs on the UI thread, including before the window exists: enumeration, sizes, deletes, launches, Drive listings, and existence checks (`DirectoryExistsAsync`/`ProbeDirectoryAsync`, never `Directory.Exists`). Watch async continuations that come back to the UI thread.
- `ProbeDirectoryAsync` answers Present, Missing or NotResponding. Never wait on the probe task after the timeout wins.
- Speech is queued and never called inline.
- Without NVDA (7.1.0): Speech is NVDA first and its path is unchanged. Only when the controller does not answer (not running, restarting, DLL missing) does `Speech.ChooseRoute` send a message as a UI Automation notification, for JAWS, Narrator and others: raised on the UI thread from the focused control of our front window, else the main window (`Speech.UseUiaHost`, set by the tray), and only when `UiaClientsAreListening`. With no window ever made (started in the tray) it stays silent. During the 2 s cache window while NVDA restarts a message can go both ways.
- The list stays virtual. Its cache is bounded. Set focus and selection with `LVM_SETITEMSTATE`. On the arrow path use `FocusedIndex`, never `FocusedItem` (it builds the item).
- Coalesce any work proportional to the selection (`_suppressStatus`, `ScheduleStatusUpdate`). Announce through `ShowStatus`, never `_statusLabel.Text`.
- An `async void` that throws kills the window, so guard new handlers. Cancel CancellationTokenSources; never dispose them.
- Use one scan (`EnumerateFileSystemInfos`).
- A row is a sentence heard on every arrow press, so each column must earn that. Say less, because NVDA already reads the row and its position.
- Never rename the focused control (`Pane.PendingName` is applied on Leave).
- Every check box is a `SpokenCheckBox`, never `new CheckBox`. On .NET 10 a stock CheckBox raises no UIA ToggleState change (measured with a UIA/WinEvent watcher), so NVDA said nothing on Space; the subclass raises it through WinForms' internal `RaiseAutomationPropertyChangedEvent` (reflection). Re-check after a .NET upgrade (two events would mean double speech).
- Every details-view ListView sets `AccessibleRole = AccessibleRole.List` (FileListView's constructor, SongPropertiesForm). Since .NET 9 it otherwise reports itself as a UIA Table, and NVDA reads only the name cell: 3.1.0 shipped that way. Check with UIA (ControlType.List) and the NVDA log ("name; Size 3 bytes; Modified today").
- Entering a folder: the app reads the first row itself (`readRowAloud` is always on). Predicting the control's event and `AccessibilityNotifyClients` both failed. Going up raises no focus event, so `SpeakRow` covers it.
- Focus the list, then the row (`FocusListAndRow`). Move focus once per action. Return focus to the list after anything modal (`RestoreListFocus`).
- Stepping up lands on the folder you came from, and returning to a folder lands on the row you left.
- `SayCount`: one item is just the verb, more says the count. `Items` gives "1 item". Shift+arrow and Ctrl+Space raise `select.count`.
- Ask for text with `PromptForm`, never `InputBox`. Size windows from `PreferredSize` (test at 1.5x font).
- Carrying on after an update or Ctrl+R (5.6.1, Conner: "can it attempt to retry the operation if conditions let it? it should ask that question"): pastes (`RunTransfer`, a wrapper over `RunTransferCore`) and Empty Google Drive trash register in `_carryable`. `ConfirmCarryOn(verb)` replaces `ConfirmAbandonTransfers` for Update and Restart: Yes means carry on afterwards (asked again only for operations started since). `CarryOnLaterAsync`, right before installing or restarting, saves them (`CarryOnTransfers`, DPAPI note in AppData, good for 30 min) and cancels the copy windows so `RemoveHalfWritten` tidies up. The next start opens the window and `CarryOnAfterRestart` re-runs each with `PasteConflictPolicy.FillGaps` once its folders answer (5 s probes, up to 5 min); emptying the trash waits for the Drive letter. Close and Quit still ask the old abandon question. Tests in Favourites530Tests.Reopen (group favourites).
- Emptying the Drive trash is one DELETE that Google answers only when done: it goes on the `_uploads` client with `EmptyTrashMost` (30 min); the quick client gave up at 15 to 30 s on a big trash.
- Transfers are modeless, unowned, one window per transfer, titled with the destination. The variable is named `window`, and a test forbids `window.ShowDialog`. Close, restart and quit ask while transfers run (`ConfirmAbandonTransfers`), except on `<exit>`.
- Watcher suppression is per destination, and the end refresh targets the destination. Each `_suppressWatch` raise sits on the line before its `try` and is released in a `finally`.
- Early progress reports are kept (`_pending`). "Cancelling" is never overwritten. Throttle progress where it is produced.
- The folder is watched, not polled (0.5s quiet, 3s max). Rows being written update in place. Only the active pane reloads. Nothing runs while the window is hidden in the tray. `RemovalWatch` lets go of a USB drive being ejected.
- Folder-size cache: mtime plus `CacheSeconds`. Automatic sizes never walk the Drive letter.
- Type-ahead is ours. Sorting matches `StrCmpLogicalW`. `TypeAhead.Fold` uses Windows `NormalizeString`, because InvariantGlobalization disables `string.Normalize`.
- Progressive load: sort on the worker and merge on the UI thread. Show nothing for 250ms. The cursor stays on its item, with one landing.
- A menu item that can only refuse is hidden, not disabled. The decision comes from the name or path only, with no reads.

## What is spoken

- `Notifications.All` lists every message. Each has one choice: speak or not. The status bar always shows it.
- Windows notifications (Conner's request, 3.0.6): any catalogued message can also show one, chosen per message in Preferences, Windows notifications (`Settings.WindowsNotifications`, ids separated by ";", empty = all off, the default). `WindowsToasts.Raise` is called from Tray `Notify` and MainForm `AnnounceOperation`; shown with the tray icon's `ShowBalloonTip`, which Windows turns into a toast. The chooser is `SpeechForm` with `windows: true`.
- `SpeechOverrides` stores only differences from default. Defaults say less, but anything requested or any failure is on (tested for fail/denied/refused).
- Every example must render. Preview plays even when the message is off. `WiredNotificationIds` must match `Raised` both ways. Never invent events.
- Routing comes from the catalogue and wording from the code. A finer id replaces the generic one. Unknown ids go to the status bar only.
- The list is the only per-message switch. Never add a tick box that gates catalogued messages (the old Speak*/AudioAnnounce* boxes did, so a message showing "Speak" never spoke). Only `SpeakEnabled` (master) and `InterruptSpeech` stay. Uncatalogued errors go out as `error.generic`. `CarryRetiredSpeechSwitches` folds old files' boxes into `SpeechOverrides`.

## Testing and probing the running app

- Assert behaviour, not this machine's setup. Registry tests snapshot and restore.
- `.editorconfig` warns on private dead code; public dead code needs a manual sweep. Test-only members such as `WinFsp.CheckLayout` stay.
- NVDA: `nvda.exe -r --debug-logging`, then read `%TEMP%\nvda.log` for `Speaking [`, then `nvda.exe -r`. Focus is not proof of speech.
- UIA finds the main window but not dialogs: use `FindWindowW("#32770", title)`. Cross-process `GetWindowText` is stale. Take a screenshot early.
- Driving keys: `AttachThreadInput` before `SetForegroundWindow`, ~120ms between characters, re-query UIA after closes, never force a disabled owner to the front.
- Debug env vars: `EXPLORERNATIVE_PERFLOG`, `_HOTKEYLOG`, `_TRANSFERLOG`, `_CONVERTBUDGET_MB` (shrinks the Drive conversion budget so small files take the read-from-Drive path; each file's route is a "convert" line in `_DRIVELOG`).
