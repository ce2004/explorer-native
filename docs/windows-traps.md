# Windows and shell traps

Part of the Explorer Native notes for Claude; CLAUDE.md says when to read this file.

## Windows and shell traps

- A bare `C:` means the current directory on C:. Launch paths go through `NormaliseLaunchPath`. Robocopy roots are `D:\.`.
- Validate typed names before `Path.Combine`.
- The Windows context menu has a time budget and is refused unless every item resolves. Escape does nothing.
- Quote `%1` in registered commands. Write an empty `DelegateExecute` with each verb.
- Default explorer: claim `open` on Directory and Drive only, in HKCU, with backups. Never `Folder`, never Win+E, never the Winlogon shell. Deregistering removes only our own values.
- The registration names only the installed copy. A non-installed copy never rewrites it.
- Pipe listener: read timeout (2s) below connect timeout (2.5s). UI posts go through `PostUi`.
