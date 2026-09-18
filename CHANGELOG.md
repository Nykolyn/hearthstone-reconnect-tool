# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-09-19

First public release.

### Added
- Always-on-top window with a **RECONNECT** button and a status line.
- **Global Ctrl+F12 hotkey** that works while Hearthstone has focus.
- **Game-server-only disconnect**: the target is read from `GameNetLogger.log`, falling back
  to port 3724. Battle.net (1119) and web ports (443/80) are never closed.
- **Automatic reverse-DNS priming** in the hosts file (/22 ranges, `.invalid` stub names). It
  removes the ~15 s stall on every reconnect and match join.
- 4-second cooldown against the double-reconnect crash.
- The version is shown in the window.
- `tools/prime-dns.ps1` to apply or remove the hosts-file fix by hand.
- `build.ps1` checks each binary for the admin manifest and embedded local paths, and can
  package a release zip with SHA-256 checksums.
- CI: every push is built, and `v*` tags publish a GitHub Release with build-provenance
  attestation.

### Security
- DLL loading is restricted to System32 from the first line of `Main`, so DLLs planted next to
  the exe (for example in *Downloads*) are not loaded with administrator rights.
- `iphlpapi.dll` and `ipconfig.exe` are loaded only by their full System32 path.
- Hosts-file priming accepts only public unicast ranges, so a tampered Hearthstone log cannot
  steer it into loopback or LAN ranges.
- Without administrator rights the app now stays idle, instead of polling once before it
  notices.
- Reproducible builds: `PathMap` and deterministic compilation keep the build machine's paths
  out of the exe.

### Fixed
- The window now scales with display DPI. At 150% scaling the text used to be clipped.
- An unexpected error during a reconnect is shown on the button instead of crashing the app.
- Holding Ctrl+F12 no longer auto-repeats.
- A disconnect could report "no active connections" when the TCP table grew between the size
  query and the read. The read is now retried.
- Process handles are released after each status poll.

[Unreleased]: https://github.com/Nykolyn/hearthstone-reconnect-tool/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/Nykolyn/hearthstone-reconnect-tool/releases/tag/v1.0.0
