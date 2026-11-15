# Shape of the code (src)

Part of the Explorer Native notes for Claude; CLAUDE.md says when to read this file.

## Shape of the code (src\)

| File | Owns |
| --- | --- |
| Program / SingleInstance / AppInstall | Entry, flags / pipe handoff, `<exit>`, ReloadSettingsCommand / install, replace, sweep |
| TrayApplicationContext | Tray, hotkeys, player, form lifetime, Quit order |
| MainForm | Window: two panes, virtual ListView, navigation, file actions (wiring); MainForm.Filter is the Ctrl+F box |
| ListEntry / ListSorting / TransferRules / SelectionSpeech / FolderFiltering | MainForm's logic as plain classes (7.1.0, group mainformlogic): the pane's row / sort order, folder orders, merge / paste rules before copying (already there, Drive and PC mixed, into itself, conflict answer, threads) / selection, status bar, empty list and size words / filter rules |
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
| BuiltInShortcuts / KeyboardShortcutsForm | Help, Keyboard shortcuts (Ctrl+F1): the fixed keys, by section (a test fails if a menu key is missing) / its list |
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
| WinKeyHook | Win+key shortcuts Windows refuses (error 1409 for 24 of 26 Win+letters): low-level hook on its own thread, posts WM_HOTKEY, 0xE8 mask key stops the Start menu, IsHeld feeds HoldRepeat; ShortcutBox captures through it while focused. Quick group winkeys (also runs shell registration) |
| GoogleDrive / GoogleAuth / GoogleSignInRequiredException | Mount, orphan sweep / OAuth, DPAPI token |
| WinFsp / WinFspSetup / DriveMount (.Volume, .Writes) / DriveLetter / DriveFileCache / LegacyCloudFiles | WinFsp bindings + CheckLayout / installs WinFsp / the volume, its answers to Windows, its write-back / letter choice / chunk cache / 4.2 sync-root cleanup |
| DriveClient / DriveWrites / DriveUpload / DriveHealth / DriveQuotaException | Drive v3 / upload, move, trash / tree upload / online state / lasting refusal |
| DriveSync / DriveMonitor / DriveMonitorForm | Folder sync planner / engine / configurator. DriveMonitor is split by role (7.1.0): DriveMonitor.cs the core (pairs, the loop, SyncOneAsync, passes, state, Dispose) / .Watcher (watchers, own writes, change journal, when a pair is due) / .Planner (listing both sides, moves, preview, local scan) / .Transfers (ExecuteAsync, lanes, uploads, downloads, trash, free up space) / .DriveOnly |
| PcChanges | Which PC folders changed: NTFS change journal, watcher fallback |
| WavDecoder / WindowsToasts | WAV, Wave64, CAF, AU, VOC, AIFF-C ima4 sound Media Foundation refuses (ADPCM, 64-bit, IMA4, ALAC in CAF) / Windows notifications per message |
| AlacDecoder / TtaDecoder / Rewrap | Apple Lossless packets / TTA files / FLAC-in-Ogg and FLV audio rewrapped for Media Foundation |
| SyncHistory / MemoryGiveBack | Per-pair history of what a sync did / handing freed memory back to Windows |
| Connect{Server,Files,Uploads,Audio,Clipboard,Details} / WebAssets / TailscaleWeb | Web app API / embedded web\ / Tailscale Serve |
| FileServers (.Sftp) / SmbFileServer / FtpServer / SftpServer / ServedFolder | Which servers run, their rules and keys / SMB over SMBLibrary / our FTP / our SFTP / path safety and sign-in throttle |
| AiDescriber / AiDescriberForm / AiSetupForm | Picture describer: providers, prices, pictures / its window / key wizard |
| Diag / BugReport | errors.log: one throttled encrypted line per caught exception (7.1.0) / --do bug-report: version, Windows, settings, scrubbed log tails (7.1.0) |
