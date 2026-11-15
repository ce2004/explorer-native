# Copying, moving, deleting

Part of the Explorer Native notes for Claude; CLAUDE.md says when to read this file.

## Copying, moving, deleting

- Robocopy and the managed engine both overwrite, so apply the conflict policy before either runs. "Ask" is resolved by the window once per paste, on top-level names. Engines report `ConflictOutcomes` with names.
- Resolve name collisions before the recursion check. Within one paste, names given out are reserved.
- Plans include folders (empty ones survive). Move junctions as links. A same-volume move is a rename. Walks skip links, not reparse points.
- Robocopy: short retries locally, long on shares. `/UNILOG` for names, `/MT` always. Call `WaitForExit()` after `WaitForExitAsync`. Robocopy skips files with matching size and mtime.
- On cancel, `RemoveHalfWritten` runs on a worker. It never touches pre-existing files, and `LooksComplete` decides what is finished (name, size, date and tail alone were all wrong).
- Recycle with FOF_WANTNUKEWARNING. Replace by writing beside the target and swapping. A clipboard retry rewinds DropEffect.
