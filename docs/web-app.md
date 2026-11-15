# The web app (Explorer Connect)

Part of the Explorer Native notes for Claude; CLAUDE.md says when to read this file.

## The web app (Explorer Connect)

- `web\` is embedded and served by ConnectServer. app.js is the only client: grep it for `/api/` before changing a route. Plain JS with no build step, and `<audio>` is the only audio path.
- Listens on 127.0.0.1 only (`WebAppPort`, 47810). HTTPS comes from Tailscale Serve on 443, never Funnel, and only our own handler is touched.
- The `Tailscale-User-Login` header is trusted only when it matches this PC's login AND the TCP peer is tailscaled.exe in session 0 (`TcpOwner.PidOf`), because any local program can send that header. Everyone else needs the pairing code. A failed `tailscale status` keeps the last good login.
- Every fetch has a timeout. Errors end in a sentence. Every async handler on the PC side has its own try/catch.
- ConnectFiles copies MainForm's Drive move and copy rules, so change both. Errors are `ConnectException`; after headers go out, just close. Unread bodies are drained.
- Web delete never deletes permanently (`HasRecycleBin`). Sizes never walk Drive or AppData. Drive files go through `ConnectStreams`/`OpenRange`, never the letter.
- Live checks over HTTP only. Drive writes go in a `zz-connect-test-<random>` folder. Save and restore the clipboard.
