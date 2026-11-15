# Commands

Part of the Explorer Native notes for Claude; CLAUDE.md says when to read this file.

## Commands (every action from the command line, 6.1.0)

Conner: "add command params to every single little action explorer native can do. from apple music, everything else."

- `ExplorerNative --commands` lists them; `ExplorerNative --do <command> [file or folder ...]` runs one in the running copy (started first when there is none). Exit code 0 when it ran, 1 with the reason otherwise. The names are in `Commands` (src\Commands.cs, in the suite).
- RULE, every time: when a feature is added or changed, its command comes with it. A new menu item needs nothing, because `MainForm.Item` registers every item it makes (`Commands.RegisterMenu`, the name is the menu text: "New &folder" is new-folder) and `MainForm.RegisterCommands` then walks both menus for items made any other way. A new `AudioActions.All` entry needs nothing either (the tray's `RegisterCommands` loops over it; PlayPause is play-pause). Anything else (a key with no menu item, something that takes a value, a new tray item) gets a `Commands.Register` line next to where it lives, in `MainForm.RegisterCommands` or the tray's. A command whose action is renamed or removed is renamed or removed with it, and CHANGES.txt says so when a name people may have scripted changes.
- A menu item's command calls its action directly, shown or hidden: the handlers refuse for themselves (`RefuseInArchive`, `RefuseForAppleMusic`). The name is taken when the item is made, so Pause/Play keeps one name.
- Paths after a command that works on a selection (`ActsOnItems`: every menu item) are gone to and selected first (`MainForm.SelectFromOutside`), then the command runs: `--do convert-audio "G:\a.flac" "G:\b.flac"`. Apple Music paths start `::applemusic`.
- `menu <menu> <item> [...]` chooses any menu item by its names, opening each menu for a moment so lists that fill in as they open (favourite folders, Apple Music sorts, playlists) are there. `go <path>`, `select <paths>`, `play <file>`, `add-to-playlist <name>`, `favourite-1` to `favourite-9`, `escape`, `switch-tab`, `context-menu`, `open-in-its-own-app`, `toggle-selection` are the ones written by hand.
- On the pipe a command is one line, `<do>` then the answer file, the name and what follows, separated by U+001F (`Commands.Encode`). `IsFolderRequest` excludes it and `Serve` runs it (`TrayApplicationContext.RunCommand`). The pipe is one way, so the answer ("ok", or a sentence) is written to a file the sender named, only ever `ExplorerNative-reply-<guid>.txt` in the temporary folder (`IsReplyPath`). "ok" means started, not finished. A window command is refused while a dialog is open, and while quitting it is answered, never kept for the next copy.
- Tests: `EXPLORERNATIVE_SELFTEST_ONLY=commands`.
