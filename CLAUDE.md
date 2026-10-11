# Explorer Native

Keyboard- and NVDA-first file manager for Windows (ARM64 + x64). WinForms, .NET 8. Two goals settle every argument: be fast, and read well under NVDA. The only packages are Concentus and NVorbis (pure IL, for Ogg/Opus). Add no others.

## Build and test

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"   # per-user SDK, not on PATH
dotnet build ExplorerNative.csproj
dotnet build tests\SelfTest.csproj
dotnet run --project tests\SelfTest.csproj   # ~1 min, "N passed, M failed", non-zero on failure
```
If the SDK is missing: `Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1; .\dotnet-install.ps1 -Channel 8.0 -Architecture arm64 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"`

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
3. Push the tag `vX.Y.Z`. `.github/workflows/release.yml` publishes win-arm64 and win-x64 with the tag's version, runs the x64 self-test (informational), and takes that version's CHANGES.txt lines as the notes (the job fails if there are none). Then `gh release create` uploads ExplorerNative-arm64.exe and -x64.exe. Help > Check for updates and Help > Changelog read these releases.
- Conner's style: CHANGES.txt, README and release notes are plain text with no Markdown symbols (no backticks, asterisks or pound signs), because NVDA reads them. Write lines for consumers, not in an AI voice: "New: ...", "Bug fixed: if you did X, Y happened. Now ...".

## The executable

- PublishSingleFile, framework-dependent (~2MB), RollForward=Major, ReadyToRun (not for Concentus/NVorbis). It needs the .NET 8 Desktop Runtime installed. Not self-contained (Conner's choice) and not trimmed (NETSDK1175).
- `Speech.LoadNvda` must probe `NATIVE_DLL_SEARCH_DIRECTORIES`, or the app is silently mute.
- Every folder open spawns a messenger. `Main` hands the path over the pipe before loading settings, speech or WinForms. Keep slow work off startup (web server, clipboard listener and audio warm run later).

## Shape of the code (src\)

| File | Owns |
| --- | --- |
| Program / SingleInstance / AppInstall | Entry, flags / pipe handoff, `<exit>`, ReloadSettingsCommand / install, replace, sweep |
| TrayApplicationContext | Tray, hotkeys, player, form lifetime, Quit order |
| MainForm | Window: two panes, virtual ListView, navigation, file actions |
| NameRules / TypeAhead / WatchDebounce / NavigationHistory | Pure name rules / jump-to-name / watcher timing / landing row |
| Settings / SettingsReset / SettingsForm / SettingChoices / ProtectedFile | Prefs + save / Reset / dialog / drop-down values / DPAPI files |
| Notifications / SpeechForm / Speech / AudioReport | Message catalogue / speak-or-off page / queued NVDA client / player diagnostics |
| FolderSizeCalculator / FileSearch / DriveSearch | Folder sizes / local search / Drive search via API |
| DriveFolderSizes | Drive folder sizes: whole-account listing + changes feed (`DriveSizeIndex`) |
| FileOperations / RoboCopyEngine / ShellDelete / ClipboardInterop | Managed copy (renames) / robocopy path / recycle and delete / clipboard |
| ConflictForm / ProgressForm / PromptForm | Paste conflict question / modeless transfer window / labelled prompt |
| ArchiveFormats / ArchiveEngine / ArchiveForm / ExtractionRules | Format table / front door / Compress dialog / safe targets |
| ArchiveBrowser | Archives opened as folders: listing tree, one-member extraction, %TEMP% cache |
| ZipEngine / TarEngine / ParallelGZip / Crc32 / BsdTar | Parallel zip / tar / gzip members / ARM CRC / Windows tar.exe |
| ShellRegistration / ShellContextMenu / ShellLink / FileLauncher | Verbs, default explorer / real context menu / .lnk / fast open |
| PerfCounters / SizeFormatter / TimeFormatter | Keystroke counters / spoken sizes / spoken times |
| Updater / UpdateForm / ChangelogForm | Manual update check / its dialog / release history |
| AudioPlayer / AudioActions / HoldRepeat | Player logic, ladders / the one action list / held keys |
| TrackDecoder / AudioDecoder / OggDecoder | Decoder choice / Media Foundation reader / Concentus+NVorbis |
| OggPages / OggOpusReader / OggVorbisPackets | Ogg pages, granule bisection / Opus / Vorbis packets |
| ExactSeek / FlacSeek / Mp3Seek / VirtualStream | Sample-exact seeking |
| AudioEncoders / AudioConvert / AudioConvertForm | Format table, WAV, own FLAC, Opus, MF sink writer / batch: gather, plan, temp then place / dialog |
| AudioEdit / AudioEditorForm | Edit list, undo history, EditedDecoder / the editor window |
| AiffStream / CdTrackStream / LocalByteStream / StreamingSource / AudioPrefetch | AIFF as WAV / CD as WAV / file as IStream / in-memory download / warm cursor file |
| TimeStretch / Resampler / SilenceReduction / Equaliser | WSOLA / pitch / silence removal / 20 bands |
| WasapiPlayback / WasapiRenderer | Ring and pump / device, gain, limiter, meter |
| Speed/Pitch/Limiter/Silence/Equaliser/Device/Formats Form | Audio dialogs, applied as you arrow |
| AudioTags / SongPropertiesForm | Property-system tags, read and written / as a list |
| TagEditing / TagEditorForm | Tag editor: merge, refusals (Drive, Ogg, playing), write, one summary / its window |
| Shortcut / ShortcutBox / ShortcutsForm / HotkeyManager / ReservedShortcuts | Key text / capture box / shortcut page / RegisterHotKey / refused keys |
| GoogleDrive / GoogleAuth / GoogleSignInRequiredException | Mount, orphan sweep / OAuth, DPAPI token |
| CfApi / DriveMount / DriveLetter / DriveFileCache | Cloud Files + CheckLayout / provider / subst / chunk cache |
| DriveClient / DriveWrites / DriveUpload / DriveHealth / DriveQuotaException | Drive v3 / upload, move, trash / tree upload / online state / lasting refusal |
| DriveSync / DriveMonitor / DriveMonitorForm | Folder sync planner / engine / configurator |
| Connect{Server,Files,Uploads,Audio,Clipboard,Details} / WebAssets / TailscaleWeb | Web app API / embedded web\ / Tailscale Serve |

## Settings

- Settings live in `%APPDATA%\ExplorerNative\settings.json`, and everything there is DPAPI-encrypted. Read it with `--read-log <name>`.
- These are hard-coded at Conner's request; never make them settings again: no navigation announcements and no size or type on select; Name, Size and Modified columns only (no Type); volume step 1, perceptual curve; held-key delay 200ms, interval 20ms, acceleration on; Drive keeps 1024MB, releases after 120s idle, warms copied files, and is on whenever signed in; window position restored; size-walk depth 64; tracks always downloaded to memory; read-ahead only for remote files; raw mode always requested.
- A setting with only one right answer is not a setting, but the feature stays at the old default. When a feature goes, its setting goes too.
- Numbers in Preferences are drop-downs. `SettingChoices.StepAt` steps by the value's magnitude.
- An update never changes a setting (since 2.0.0). `Migrate` changes nothing, `Validated` only repairs broken values and never narrows a range, and new settings arrive at their default (`UpdatesNeverChangeSettingsTests`). New installs have no global shortcuts.
- `NoNullStrings` runs before `Migrate`, because one null used to reset everything. An unparseable file is salvaged property by property and backed up as `.damaged-*`. Unknown keys from newer builds are kept.
- OK applies only what the dialog changed (`WithChanges`), and an external reload does the same (`ReloadOnto`).
- Anything that edits settings.json from outside must send `ReloadSettingsCommand`. Otherwise the app's save-on-exit overwrites it (this once deregistered every folder). Save the return value of `ShellRegistration.Reconcile`.
- `settings.json.*` matches `settings.json` itself on Windows.
- Tests use `Settings.OverrideAppDataDir` and `PersistedProperties()` (Public|Instance). Plain `GetProperties()` includes statics and would retarget the real file.

## UI and NVDA rules

- Nothing slow runs on the UI thread, including before the window exists: enumeration, sizes, deletes, launches, Drive listings, and existence checks (`DirectoryExistsAsync`/`ProbeDirectoryAsync`, never `Directory.Exists`). Watch async continuations that come back to the UI thread.
- `ProbeDirectoryAsync` answers Present, Missing or NotResponding. Never wait on the probe task after the timeout wins.
- Speech is queued and never called inline.
- The list stays virtual. Its cache is bounded. Set focus and selection with `LVM_SETITEMSTATE`. On the arrow path use `FocusedIndex`, never `FocusedItem` (it builds the item).
- Coalesce any work proportional to the selection (`_suppressStatus`, `ScheduleStatusUpdate`). Announce through `ShowStatus`, never `_statusLabel.Text`.
- An `async void` that throws kills the window, so guard new handlers. Cancel CancellationTokenSources; never dispose them.
- Use one scan (`EnumerateFileSystemInfos`).
- A row is a sentence heard on every arrow press, so each column must earn that. Say less, because NVDA already reads the row and its position.
- Never rename the focused control (`Pane.PendingName` is applied on Leave).
- Entering a folder: the app reads the first row itself (`readRowAloud` is always on). Predicting the control's event and `AccessibilityNotifyClients` both failed. Going up raises no focus event, so `SpeakRow` covers it.
- Focus the list, then the row (`FocusListAndRow`). Move focus once per action. Return focus to the list after anything modal (`RestoreListFocus`).
- Stepping up lands on the folder you came from, and returning to a folder lands on the row you left.
- `SayCount`: one item is just the verb, more says the count. `Items` gives "1 item". Shift+arrow and Ctrl+Space raise `select.count`.
- Ask for text with `PromptForm`, never `InputBox`. Size windows from `PreferredSize` (test at 1.5x font).
- Transfers are modeless, unowned, one window per transfer, titled with the destination. The variable is named `window`, and a test forbids `window.ShowDialog`. Close, restart and quit ask while transfers run (`ConfirmAbandonTransfers`), except on `<exit>`.
- Watcher suppression is per destination, and the end refresh targets the destination. Each `_suppressWatch` raise sits on the line before its `try` and is released in a `finally`.
- Early progress reports are kept (`_pending`). "Cancelling" is never overwritten. Throttle progress where it is produced.
- The folder is watched, not polled (0.5s quiet, 3s max). Rows being written update in place. Only the active pane reloads. Nothing runs while the window is hidden in the tray. `RemovalWatch` lets go of a USB drive being ejected.
- Folder-size cache: mtime plus `CacheSeconds`. Automatic sizes never walk the Drive letter.
- Type-ahead is ours. Sorting matches `StrCmpLogicalW`. `TypeAhead.Fold` uses Windows `NormalizeString`, because InvariantGlobalization disables `string.Normalize`.
- Progressive load: sort on the worker and merge on the UI thread. Show nothing for 250ms. The cursor stays on its item, with one landing.
- A menu item that can only refuse is hidden, not disabled. The decision comes from the name or path only, with no reads.

## Audio player

There is no window: Enter, the Audio menu, Preferences pages and global hotkeys. Shift+Enter opens the file in its own app. No playlist.

- Silent by default. `SayAudio` always writes the status bar and speaks only when allowed. "What is playing" and failures always speak.
- The menu, shortcut page and tray all come from `AudioActions.All`. Toggle labels show the next action. Access keys are unique (tested). Use `ShortcutKeyDisplayString`, never ShortcutKeys.
- Hotkeys: no bare keys except media keys. `ReservedShortcuts` blocks keys whose loss would leave no way to do the thing: window keys, Ctrl+C/Z, Shift+arrow, Ctrl+Space, Space, bare F1-F24. Ctrl+arrow is allowed on purpose. Check from both the capture box and the registration. Read the real settings.json before reserving more.
- Never default to Ctrl+Alt+arrow: NVDA's hook swallows it silently. The tray menu has play/pause, stop and "what is playing" for that reason.
- Settings-file mistakes (reserved, duplicate, no modifier) get one modal dialog, posted through `_ui.Post`. "Taken by another app" stays a notification. Debug with `EXPLORERNATIVE_HOTKEYLOG`.
- MOD_NOREPEAT on every hotkey; `HoldRepeat` does the repeating. It paces by how long the action took, detects release with `GetAsyncKeyState`, and stops after 30s. Skipping uses `NudgeRepeatMilliseconds` with no acceleration. Speed has its own 120ms clock.
- Seek counts from its own last target and applies every press. Do not bring back the seek throttle. Skipping past the end restarts at 0.
- Volume above 100% is our own multiply. Never touch endpoint volume, never use an MFT. Float is not clamped; `ClippedPercent` reports clipping, reset per track. Volume is also saved on Quit.
- The limiter is an upward compressor (ceiling 0.9), active only above gain 1 unless `LimiterWholeRange`. One envelope for all channels, 0.5ms attack, 0.25s release, coefficients from the device rate. Its controls live in LimiterForm only, and opening the form turns nothing on.
- At rest the whole chain is bit-exact (`NeutralChainTests`): gain 1, EQ off or flat, stretch rate 1, pitch 1, silence reduction off are all bypasses.
- Changing device opens the new one before stopping the old. The ring and decoder survive. An empty id follows the system default (`IMMNotificationClient`). `GetDevice` succeeds for unplugged endpoints, so check `GetState`. Never name a file in a device error. Default changes are queued (`DefaultMoved`), not tracked with flags.
- When paused, the device stops (`SetSuspended`) and the pump waits on an event.
- Errors name the real cause (`Diagnostic`), not "cannot decode". The audio page shows what was granted, and failures are appended, not replacing the state.

Pipeline: decoder -> SilenceReduction -> TimeStretch -> Resampler -> ring -> renderer (gain, limiter, EQ, meter)
- Media Foundation `IMFSourceReader` first. `OggDecoder` covers Ogg/Opus, because Web Media Extensions are invisible to unpackaged apps. The media engine and gain MFT are deleted: the MFT took every sample and output none. Never retry that.
- TimeStretch (WSOLA): 1024 hop, 50% overlap, raised-cosine fade, ±640 search on a mono sum, fractional remainder carried, end residue emitted proportionally. Test pitch (Goertzel) and purity (≥0.98).
- Pitch: resample by P and stretch by S/P, both set in `ApplyRates`. The resampler comes after the stretcher. Catmull-Rom, anti-alias only when pitching up, fraction carried.
- EQ: 20 half-octave bands, ±24dB, Q 2, end shelves cornered between bands, double-precision TDF-II, 20ms glides, placed after the limiter. Test that neighbouring bands do not move.
- Silence reduction runs before the stretcher. It uses spin boxes because an editable combo went silent and a full list took 1s to open. Values are spoken 400ms after arrowing stops. Detection blocks are at most 128 frames. The clock overshoot across a cut is known and accepted.
- Ring: 4s capacity, kept shallow (0.2s) unless a download is still arriving (`Available < Length`, not `Complete`). Prebuffer floor is `Math.Min(PrebufferFrames, TargetFrames())`. The renderer opens suspended and `ReleaseWhenBuffered` is checked every pump loop.
- Gapless repeat: the pump loops the decoder itself (`WasapiPlayback.Loop`). Two empty loops end the track. `_lastPassFrames` fixes the clock across the loop.
- `IsPlaying` asks the ring. An empty read is not the end (`EmptyReadsBeforeEnd` 3). Drop a block if a newer seek is waiting.
- COM: wrong slot means a wrong call. `[PreserveSig]` for double/BOOL returns, BOOL as int. Never inherit `[ComImport]` interfaces; repeat the methods. `IMFMediaType.SetUINT32` is slot 18. Arrays marshal as SAFEARRAY, so use IntPtr. Create COM objects on the thread that uses them.
- `AudioDecoder` counts MFStartup per instance. An unbalanced MFShutdown kills MF for the whole process.
- Exact seeks land on the sample in every format except WMA (~1ms). FLAC and VBR MP3 locate their own frames. Searches use only bytes already held. Seeks start on the resampler grid (`GridStep`) and on AAC frames. Opus run-up is 500ms. MP3 joins drop a frame; LAME "Info" decodes as silence. NVorbis needs `TrailingShift`.
- Ogg: our own page readers (the library readers index the whole file). Sniff the first packet (OpusHead or vorbis). An unreadable head is "unknown, try Opus then Vorbis", never "no". Every empty-read loop is bounded.
- .mp4/.mov/.mkv/.avi play their first audio stream. `.cda` is read as raw CDDA through `CdTrackStream`. AIFF goes through as WAV. Tags come from the property system; blank means absent, and they are read on a worker.
- Remote files: `StreamingSource` plays through an in-memory download window (4MB pieces, first MB alone). One track at a time. The buffer is released after 10 minutes paused. `StartFilling` always allocates. Reads are async and cancellable, never under the lock. A generation number replaces thread joins. `ReadAt` retries 20s from the first failure. Never restore the reverted "Prioritise" moving window.
- `AudioPrefetch` waits 350ms of settle, handles remote files only, and is skipped for bulk selection and Drive. Drive tracks read straight from Google (`RangeSourceFor` -> `OpenRange`), never through the placeholder.
- Rejected after measurement: parallel SMB readers, MF real-time mode (dropouts). Measure shares with interleaved A/B and medians.
- Tests need real audio (`Tests.WriteTone`), since silent fixtures hide silent bugs. Watch a new test fail against the bug first. Fixtures are generated in code. Every audio harness gets a 60s watchdog.
- Measuring: use the per-session meter and sweep every endpoint. A missing session is not proof of no sound; a moving clock is the proof.

## What is spoken

- `Notifications.All` lists every message. Each has one choice: speak or not. The status bar always shows it. No tray balloons.
- `SpeechOverrides` stores only differences from default. Defaults say less, but anything requested or any failure is on (tested for fail/denied/refused).
- Every example must render. Preview plays even when the message is off. `WiredNotificationIds` must match `Raised` both ways. Never invent events.
- Routing comes from the catalogue and wording from the code. A finer id replaces the generic one. Unknown ids go to the status bar only.

## Google Drive (Cloud Files provider on a subst letter)

Our own provider over `cldapi.dll`, so the app's ordinary file code works on the letter. Scope is full `drive`, so bugs can damage the account. Off by default.

- Placeholder enumeration costs ~15ms each and never caches. Never walk the letter: panes use the mount's listing (`TryListing`). F5 forgets that listing first. Listing names are the on-disk names after `Sanitise`/`Unique` (`DrivePlaced`).
- Requests cost ~700ms each. Callbacks are served from `DriveFileCache` (4MB chunks, 1GB pool). Cold reads wait 400ms, then fetch at most 1MB themselves. `MaxOpenFiles` is 3. `SweepIdle` never runs during callbacks. Caches are leased.
- A cache does not download in its constructor: it starts when warmed (leased and let go unread) or after ~1MB of straight reads, so a tag or thumbnail read costs one small range. Read-ahead is a sliding window of 4, bounded past the furthest chunk read, retrying a failed chunk.
- Folder sizes on the letter come from `DriveSizeIndex` (DriveFolderSizes.cs): one listing of the account, split by modifiedTime slices 8 at a time, then the changes feed every 20s. One index per drive. Never size a Drive folder by walking it.
- `Populate(..., interactive)`: interactive (callbacks, navigation, search, web app) uses the 15s client, 2 tries, no outage wait; patient (uploads, CreateFolderIn, NamesIn, IdOrRelist, ChildrenOnDrive, PopulateRoot) waits blips out. A patient caller made interactive can create duplicate folders on a blip. CreateFolderIn refuses when the parent has no listing.
- Offline with a cached listing answers from it, without marking the folder fully populated; back online, those folders are listed again.
- A populated folder is marked DISABLE_ON_DEMAND_POPULATION, so outside programs get it without a listing. `Forget` and a 30s timer (`ReopenStale`) hand it back (ENABLE 0x08) so Explorer's F5 re-lists. The root is never handed back. Renames on the web rename the placeholder; vanished items' placeholders are deleted. Rename/move use `MoveLocal` before falling back to RemoveOne+PlaceOne.
- Fetch-data is sent in 4MB pieces as they arrive, with long arithmetic (ranges over 2GB).
- Never call `GetOrAdd(key, factory)` for caches or folders. Use the value overload or `Lazy`.
- Silently wrong: MARK_IN_SYNC on the root or folders (empty drive); `CfConvertToPlaceholder` on the root (use DISABLE_ON_DEMAND_POPULATION_ON_ROOT); POPULATION_POLICY_FULL; logging 0x8007018E as a failure.
- Check flag values against `cfapi.h`. `CheckLayout` compares struct sizes (registration 72, CALLBACK_INFO 152, OPERATION_INFO 48). Never remove interop fields that look unused.
- A failed read must name `RequiredFileOffset`/`RequiredLength`. A zero length leaves the reader unkillable until reboot. Callbacks have deadlines (45s/90s). Never answer a short read as success, and never take a 200 as a range.
- `StandDownReads(pid)` before killing robocopy. Unmount fails in-flight reads first.
- Quit order: player and prefetcher, then `WaitForReads`, then `DriveMount.Dispose` (`WaitForCallbacks`), each bounded at 5s.
- Never probe through the window or read G: from your own process. Stuck readers are unkillable. Use UseShellExecute plus an output file, never `Environment.Exit` mid-read, and use `dotnet-stack`.
- `CfHydratePlaceholder` times out by design (STREAMING_ALLOWED). Writes into G: from other programs never reach Drive.
- Every write branches on `Drive.Owns` (CreateFolderIn, CreateFileIn, RenameOnDrive, delete, paste).
- Delete goes to the Drive trash, and the placeholder is removed only after Drive agrees. Verify by restarting.
- Moves are one PATCH. A move out trashes only what `VerifyCopied` shows arrived. Check permissions first. Refuse selections mixing Drive and local items.
- Copy inside Drive is server-side `files.copy`, with folders recreated. Settle the name first. Overwrite means copy under a free name, then trash the old. Never swap file and folder, never copy into itself. A destination that will not list fails rather than duplicating.
- Names are not keys on Drive. Always check conflicts, and "could not list" means ask. Replace means upload, then trash. Folders merge. `PlaceOne` seeds `Unique` from the listing.
- A retried create first looks for its own `appProperties` tag. If it cannot look, it does not resend.
- Uploads stream. On failure, query the session and rewind to Google's count. A 308 with no Range means zero bytes held. Small files get 6 tries (408/429/5xx only, jittered), with stream and request rebuilt each try. Uploads use a stall watchdog, not a timeout. 4 file streams, 8 folder streams. Empty folders are created and links are skipped.
- `Explain` error bodies (never dump HTML). Cap error lines at 300 characters, cutting from the end.
- Auth: built-in client (100-user cap), with a client JSON file taking precedence. In production, so no weekly expiry. Loopback redirect needs `ContentLength64`. Bump `ScopeVersion` on any scope change. `_tokenGate` serialises refreshes. Consent is per call, never inside a filesystem callback. Only `invalid_grant` deletes the token. Sign-out is local.
- A bare TaskCanceledException is our own cancellation, not "offline".
- `SweepOrphans` runs every launch using `QueryDosDeviceW`. Unregistered leftover roots (error 203) cannot be removed, so step aside to `GoogleDrive-2`.
- The subst letter reports the host disk's size, so we substitute Google's quota.
- Shared drives: a virtual folder, `supportsAllDrives=true` on every request, listings carry `driveId`, nothing is created in the virtual list, search covers one drive at a time.
- Search goes through the API with a cached folder graph and `RootId`. Paths come from ids (`PathForChain`). The local walk uses an explicit stack, stops at a cap and says so.
- Copy to clipboard warms the file; cut does not. Copy out uses robocopy `/A-:O /DCOPY:T` with one thread. Progress is the union of ranges served.
- `RefreshOne` updates changed placeholders (not SUPERSEDE). `ForgetGone` drops vanished ids.
- `Sanitise` substitutes rather than trims: illegal characters, empty-after-trim, device names (`CON_.flac`), names over 255. Google Docs are skipped.

## Drive monitor (folder sync)

- Pairs (PC folder, Drive folder, mode, CopyDeletes, Paused) are in `Settings.DriveSyncPairs`. State is in `drive-monitor\<id>.json`. `SyncPlanner` is pure and tested. The engine is `DriveMonitor`; the configurator is under File, shown only while Drive is enabled.
- Runs only while mounted, one pair at a time, through the API only.
- Every pass lists both sides; events only make a pass due. State is saved every 2s, and the planner is idempotent.
- Downloads go to `.partial`, resume, and are verified. Uploads resume (sessions dropped after 6 days). The change test is size plus mtime within 2s. Conflicts are renamed `name (conflict yyyy-MM-dd).ext`.
- First sync adopts matching files (md5 or size plus mtime). State is keyed by pair id.
- Space margin is 2GB or 5%. Google's "not now" errors are waited out for at most an hour per action (daily limits excepted), then the file counts as failed. Big passes announce progress.
- Never overwrite or delete on the strength of the scan alone: re-check the local file and the Drive item just before replacing, recycling or trashing. A changed local file is recycled before a download replaces it.
- A PC edit updates the Drive file in place (same id, keeps revisions). Files modified in the last 10s or open for writing wait for a later pass.
- Pause/Remove/Edit stop a running pass before its next action. An unreadable state file is set aside and the pair paused, never treated as a first sync. Duplicate same-name Drive folders are a clash and skipped.

## The web app (Explorer Connect)

- `web\` is embedded and served by ConnectServer. app.js is the only client: grep it for `/api/` before changing a route. Plain JS with no build step, and `<audio>` is the only audio path.
- Listens on 127.0.0.1 only (`WebAppPort`, 47810). HTTPS comes from Tailscale Serve on 443, never Funnel, and only our own handler is touched.
- The `Tailscale-User-Login` header is trusted only when it matches this PC's login AND the TCP peer is tailscaled.exe in session 0 (`TcpOwner.PidOf`), because any local program can send that header. Everyone else needs the pairing code. A failed `tailscale status` keeps the last good login.
- Every fetch has a timeout. Errors end in a sentence. Every async handler on the PC side has its own try/catch.
- ConnectFiles copies MainForm's Drive move and copy rules, so change both. Errors are `ConnectException`; after headers go out, just close. Unread bodies are drained.
- Web delete never deletes permanently (`HasRecycleBin`). Sizes never walk Drive or AppData. Drive files go through `ConnectStreams`/`OpenRange`, never the placeholder.
- Live checks over HTTP only. Drive writes go in a `zz-connect-test-<random>` folder. Save and restore the clipboard.

## Copying, moving, deleting

- Robocopy and the managed engine both overwrite, so apply the conflict policy before either runs. "Ask" is resolved by the window once per paste, on top-level names. Engines report `ConflictOutcomes` with names.
- Resolve name collisions before the recursion check. Within one paste, names given out are reserved.
- Plans include folders (empty ones survive). Move junctions as links. A same-volume move is a rename. Walks skip links, not reparse points.
- Robocopy: short retries locally, long on shares. `/UNILOG` for names, `/MT` always. Call `WaitForExit()` after `WaitForExitAsync`. Robocopy skips files with matching size and mtime.
- On cancel, `RemoveHalfWritten` runs on a worker. It never touches pre-existing files, and `LooksComplete` decides what is finished (name, size, date and tail alone were all wrong).
- Recycle with FOF_WANTNUKEWARNING. Replace by writing beside the target and swapping. A clipboard retry rewinds DropEffect.

## Compressing and extracting

- Context-menu commands only. `ArchiveFormats.All` is the format list. We do zip, tar and gzip ourselves; Windows tar does xz, bz2, zst and 7z, plus read-only formats.
- System32 tar (libarchive 3.8.8) crashes on non-ASCII names. `BsdTar.Executable` falls back to a WinSxS copy and caches the choice. A fresh install may have no older copy (this dev VPS): tests gate on `BsdTar.HandlesNames`, and the crash is reported in words.
- Only a source file that cannot be read is skipped; any failed write to the archive aborts it, so a Replace never puts a partial archive over a good one.
- libarchive loses non-ASCII names through text, so names go only as arguments or straight to disk. Never list bsdtar formats by name.
- Pass the format explicitly (`FormatFlags`), extract with `-k`, use junctions for long paths, judge stalls by CPU time. Stage into `.extracting-*` in the destination.
- Parallel deflate. One worker and sixteen must give byte-identical output. This machine runs ~3.8 cores.
- `SafeTarget` checks containment (UNC before trimming slashes). Folders are plan entries. Zip64 only when needed. UTF-8 flag on. CRC-32 is the ARM `Crc32` instruction, not `Crc32C`.
- Progress counts while compressing. Test progress with one file. Unreadable files are skipped, not fatal. Every archive written is round-tripped through bsdtar.
- Browsing (Enter on an archive): `Pane.Archive` is set and `CurrentPath` is a virtual path through the archive (`C:\x\a.zip\Disc 1`). Never probe, watch or read it as a folder; every command that writes refuses with `RefuseInArchive`, and its menu items are in `_archiveHidden`. Zip and tar are listed from their headers and one member is extracted on its own (`ExtractionRules.Selecting`, so SafeTarget still applies). bsdtar formats are extracted whole into `tree-*` in the cache, because their names are only right on disk. Not on the Drive letter, and not .iso (Windows mounts those).

## Converting and editing audio

- Context-menu commands (Convert audio, Edit audio), shown by name only. Not on the Drive letter.
- Decode with TrackDecoder, encode: WAV and Opus (Concentus OpusOggWriteStream) ourselves; MP3, AAC and FLAC through the MF sink writer, which picks the container from the extension. This is encoding, not the playback chain, so the no-MFT rule does not apply. FLAC falls back to our own encoder (fixed predictors, Rice) when Windows has none. Encoders are probed with MFTEnumEx.
- Output goes to `.converting-*` with the target's extension and is moved into place only when complete. Never over a source, never over another source in the batch; replacing an existing file only when the person chose Replace (asked once per batch, `ConflictForm` with `filesOnly`).
- `AudioDecoder`'s MFStartup count and call are one step under a lock: the converter opens decoders in parallel, and the old Interlocked count let a second decoder use MF before the first had started it (0xC00D3E85).
- Windows reads no tags from WAV and refuses to write them; the summary says so. Opus tags go in OpusTags, FLAC (ours) in VORBIS_COMMENT, the rest through the property store after writing.
- The editor never writes until save. Its `EditList` is immutable (undo keeps the old one); `EditedDecoder` plays and renders it, through `WasapiPlayback.DecoderFactory`. Save writes "name (edited).ext"; Ctrl+Shift+S replaces the original only after a question, original to the Recycle Bin first.

## Windows and shell traps

- A bare `C:` means the current directory on C:. Launch paths go through `NormaliseLaunchPath`. Robocopy roots are `D:\.`.
- Validate typed names before `Path.Combine`.
- The Windows context menu has a time budget and is refused unless every item resolves. Escape does nothing.
- Quote `%1` in registered commands. Write an empty `DelegateExecute` with each verb.
- Default explorer: claim `open` on Directory and Drive only, in HKCU, with backups. Never `Folder`, never Win+E, never the Winlogon shell. Deregistering removes only our own values.
- The registration names only the installed copy. A non-installed copy never rewrites it.
- Pipe listener: read timeout (2s) below connect timeout (2.5s). UI posts go through `PostUi`.

## Testing and probing the running app

- Assert behaviour, not this machine's setup. Registry tests snapshot and restore.
- `.editorconfig` warns on private dead code; public dead code needs a manual sweep. Test-only members such as `CheckLayout` stay.
- NVDA: `nvda.exe -r --debug-logging`, then read `%TEMP%\nvda.log` for `Speaking [`, then `nvda.exe -r`. Focus is not proof of speech.
- UIA finds the main window but not dialogs: use `FindWindowW("#32770", title)`. Cross-process `GetWindowText` is stale. Take a screenshot early.
- Driving keys: `AttachThreadInput` before `SetForegroundWindow`, ~120ms between characters, re-query UIA after closes, never force a disabled owner to the front.
- Debug env vars: `EXPLORERNATIVE_PERFLOG`, `_HOTKEYLOG`, `_TRANSFERLOG`.
