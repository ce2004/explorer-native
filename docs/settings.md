# Settings

Part of the Explorer Native notes for Claude; CLAUDE.md says when to read this file.

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
- Export and import (Preferences beside Reset, no access keys there because the dialog keeps only O, C and R; File menu with &Export and I&mport): `Settings.ToExportJson` writes plain JSON starting with `ExportMarker` with EVERY persisted setting (Conner, 4.2.3: "export every option, everything": pairing code, start folder, sound device, Apple Music token included) plus the Google client ID/secret from client_secret.json under `ExportGoogleClient`. `ThisPcOnly` is now only what import never takes (exe path, FoldersTakenAt, window, last folders, tabs, focus, last track). `FromExportJson` goes through Salvage and Validated like settings.json and refuses anything without the marker or SettingsVersion. `PrepareImport` carries this PC's values over and pauses pairs whose LocalFolder is not here. MainForm saves the client with `GoogleAuth.SaveClient` (forgetting the sign-in if the client changed) and applies with `Program.ApplySettings`. A test sets each setting alone to a non-default value and round-trips it, so a new setting cannot be left out. Tests: `EXPLORERNATIVE_SELFTEST_ONLY=settingstransfer`.
- Tests use `Settings.OverrideAppDataDir` and `PersistedProperties()` (Public|Instance). Plain `GetProperties()` includes statics and would retarget the real file.
