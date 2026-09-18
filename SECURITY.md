# Security

HS Reconnector runs **as administrator**. Code with that much access should be easy to audit, so
this page lists everything it does.

## What the app does with administrator rights

| Action | Scope |
|---|---|
| Closes a TCP connection (`SetTcpEntry`, `DELETE_TCB`) | Only connections owned by `Hearthstone.exe`, and never Battle.net (1119) or web ports (443/80). |
| Edits the hosts file | Only inside its own `# BEGIN HsReconnector` / `# END HsReconnector` block. Entries are stub names under the reserved `.invalid` TLD for public game-server ranges, at most four /22 ranges. The original file is backed up once to `hosts.hsreconnector.bak`. |
| Runs `ipconfig /flushdns` | Started by its full System32 path, right after a hosts change. |

## What it reads

- The tail of Hearthstone's `GameNetLogger.log` / `Hearthstone.log`, to find the game-server
  address. Parsed as untrusted input.
- The Hearthstone install location, from the running process or the uninstall registry key.
- The list of TCP connections, filtered to Hearthstone's own.

## What it never does

- No network requests, telemetry, auto-update, or downloads.
- No settings or other files written, except the hosts block above.
- No access to Battle.net credentials, game memory, or game files.
- No DLLs loaded from outside System32 once `Main` starts (`SetDefaultDllDirectories`).

## Where to keep the exe

Like any program that runs elevated, the exe is only as safe as the folder it sits in. Windows
loads a few DLLs during process start-up, before any of the app's code runs. If a malicious DLL
with the right name sits next to the exe, as can happen in a *Downloads* folder, it would run
with administrator rights. Keep `HsReconnector.exe` in a folder only administrators can write to,
for example `C:\Program Files\HS Reconnector\`.

## Verifying a release

Each release is built by GitHub Actions from the tagged commit. Compilation is deterministic
(`Deterministic` + `PathMap`), so the exe carries no machine-specific paths. Reproducing it byte
for byte also requires the same .NET SDK version as the CI run, which is shown in the workflow log.

- **Attestation:** `gh attestation verify HsReconnector.exe --repo Nykolyn/hearthstone-reconnect-tool`
  proves the exe was built by this repository's workflow.
- **Checksum:** compare `Get-FileHash HsReconnector.exe` with `SHA256SUMS.txt` from the same
  release.
- **Source:** build it yourself with `build.ps1`.

## Supported versions

Only the latest release gets fixes.

## Reporting a vulnerability

Please **do not** open a public issue. Use
[private vulnerability reporting](https://github.com/Nykolyn/hearthstone-reconnect-tool/security/advisories/new)
(*Security → Report a vulnerability*). Include the version, your Windows version, and steps to
reproduce. Expect an acknowledgement within a week.
