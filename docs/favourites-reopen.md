# Favourite folders and reopening after an update

Part of the Explorer Native notes for Claude; CLAUDE.md says when to read this file.

## Favourite folders and reopening after an update (5.3.0)

- `Settings.FavouriteFolders` (List of paths, at most `Favourites.Most` 9, repaired by `Favourites.Clean` in Validated; `CopyOf`/`SameValue` know `List<string>`). Rules in src\Favourites.cs (Key, IndexOf, Add, Move, Label with twins shown as the whole path), dialog src\FavouritesForm.cs (changes kept at once through `Changed`). MainForm: Fa&vourites menu after Go (rebuilt on DropDownOpening between two separators), Ctrl+D and Ctrl+Shift+1 to 9 in `ProcessCmdKey`; going to one announces nothing (no navigation announcements). Not the drives list or inside an archive (`FavouriteHere`). Tests: `EXPLORERNATIVE_SELFTEST_ONLY=favourites`.
- `ReopenAfterUpdate` (src\ReopenAfterUpdate.cs): the tray's `<exit>` handler writes `%APPDATA%\ExplorerNative\reopen-window` when the window is visible; the next start takes it (good for 10 min) and shows the window even with StartMinimised. Folders and tabs come back from settings as on any start (Conner: an update put him back in the tray).
