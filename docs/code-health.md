# Code health: errors.log, blocking, CI, bug reports, timings (7.1.0)

Part of the Explorer Native notes for Claude; CLAUDE.md says when to read this file.

7.1.0 came from a review of the whole codebase at 7.0.1 (Conner's issue file). These are the rules it left.

## errors.log (src\Diag.cs)

- `Diag.Note(ex)` writes one line to `%APPDATA%\ExplorerNative\errors.log`: time, file and method (CallerFilePath, CallerMemberName), exception type, HResult, the first 200 characters of the message. Encrypted a line at a time like every other log; read it with `ExplorerNative --read-log errors.log`.
- It never throws and never blocks: the line is queued and written on a worker. The same file, method and exception type is written at most once a minute (`Quiet`), at most `LinesPerMinute` (120) lines a minute in all, and past `MaxBytes` (1 MB) the file becomes errors.old.log. OperationCanceledException is never written: it is how work stops.
- Use it in any catch that lets an exception go, unless the catch is truly best effort: temp-file cleanup, Dispose of something already failing, per-row, per-sample or per-packet loops, COM Release, the logging code itself. Never change control flow to add it; keep the `when` filter and the comment.
- Tests: group `diag` (tests\DiagTests.cs); `Diag.Enabled`, `Diag.Flush`, `Diag.ResetForTests`, `Settings.OverrideAppDataDir`.

## The bug report (src\BugReport.cs)

- `ExplorerNative --do bug-report [file or folder]` writes one plain text file (default: the desktop, "Explorer Native bug report <date time>.txt"): version, Windows edition and build, process and OS architecture, .NET, whether this is the installed copy, the settings as `--do settings` shows them (secrets only say set or not set), then the last lines of errors.log, errors.old.log, crash.log and install.log, decrypted.
- Every line is scrubbed (`BugReport.Scrub`): the values of every secret setting and the Google client secret wherever they appear, Google access and refresh tokens, client secrets and API keys, sk- keys, Bearer headers, JWTs, name=value and "name": "value" where the name says password, token, secret, key, cookie or authorization (but not "= set" or "= not set"), key-bearing query strings, and email addresses (sign-ins).
- Program runs it in the copy that was launched, before anything is sent down the pipe, because it is wanted most when the running copy is stuck or crashed. It is also in `Commands` so `--commands` lists it. Tests: group `bugreport`.

## The CI server run (tests\ServerRun.cs)

- `EXPLORERNATIVE_SELFTEST_SERVER=1` is the run that gates every release (release.yml `test` job) and every push to main (ci.yml). `Check` turns a failure into SKIP, printed with its reason, only when `ServerRun.Excuse` names a reason that is really true of that machine: no audio device (Windows lists no playback endpoint, and the detail is one of the no-device symptoms), elevated (permission checks an administrator cannot fail), shared timing (two named timing checks and the describer window's timer-driven conversation), no signed-in desktop (HKCU Run).
- RULE: never add a check to ServerRun to get a release out. Fix the test or the code. Add one only when the runner itself makes the check impossible, and write why.
- The 7.0.1 release run (the last before the gate) had 9,313 passed and 40 failed, all of them in those categories.

## Timings (tests\PerfTimings.cs)

- Group `perf`, and the end of the whole run: `TIMING <name> <ms> ms` lines, each the median of five, the same names every release: `startup` (the exe to its `--help` answer, only with `EXPLORERNATIVE_PERF_EXE`), `open-folder-10000` (one scan of 10,000 files and the pane's logical sort), `search-10000` (FileSearch.Local for one name), `drive-first-listing` (a fresh DriveClient listing 2,000 files from FakeDrive). On GitHub the table also goes to the job summary. Nothing fails for being slow; compare the lines between releases.
- This PC, 7.1.0: open-folder-10000 about 25 ms, search-10000 about 9 ms, drive-first-listing about 20 ms, startup about 45 ms warm.
- The suite runs with `Diag.Enabled` off (Tests.Main), so its own failures never land in the real errors.log. A test of something that logs turns it on with `Settings.OverrideAppDataDir` set to a temp folder, as DiagTests does.
- 7.1.0 first pass: 128 of the 517 empty catches outside MainForm, DriveMonitor, the tray, Google, Apple Music, servers and Program now log; 389 stay empty in the best-effort categories above. The excluded files (314 empty catches) were done after merging, see below.

## Blocking and threading (7.1.0 audit)

- Bounded checks on the UI thread are awaited with `Task.WhenAny(probe, Task.Delay(n))`, never `probe.Wait(n)` (ArchiveForm.Accept was the one that did; now a guarded async void that returns if the dialog closed meanwhile).
- No .Wait, .Result, GetResult or Thread.Sleep sits inside a lock block anywhere in src; the hits are Monitor.Wait, which releases the lock.
- Accepted UI-thread waits, by design: Settings.Load/Save retry sleeps on a sharing conflict (at most about 375 ms), ProtectedFile.AppendLine retries on a sharing violation (up to 2 s), the quit path's bounded waits (tray ClearOpened 5 s, the Drive drain, AudioPlayer.Dispose 5 s, AudioEditorForm.WaitForReplaces 10 s), and Program's startup GetResult on settings loading before any window exists.
- Everything else found (ConnectServer, DriveMount, WinFsp dispatcher, audio pump, Radio, StreamingSource, encoders, SingleInstance pipe) runs on its own thread or a worker, or reads a task already finished.
- Second pass (7.1.0): the tray, Google, Apple Music, servers, Program, Commands, Speech, Preferences and the Drive monitor window: 62 of their 204 empty catches now log. The servers never log per-connection reads or a client hanging up; they log a failed start and an accept failure that is not a SocketException. A wait that may time out logs everything except TimeoutException (GoogleDrive's size refresh). AppleMusicPlayer PlayAutoplay and PlayNextFromStation (async void, not UI handlers) are wrapped whole.
- Also checked and found on workers, not the UI thread: GoogleAuth AdoptFrom's `_tokenGate.Wait()` (after a real network await with ConfigureAwait(false)), GoogleDrive StreamInto (AudioConvert worker) and OpenRange's GetResult (web server threads, the player's open worker, the describer inside Task.Run), ServedFolder.Slow's sleep (connection threads, Braking releases its lock first).
