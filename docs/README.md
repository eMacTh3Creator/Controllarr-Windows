# Controllarr for Windows Docs

Setup guides, operating instructions, and release notes for Controllarr on Windows.

## Start Here

- [MOBILE.md](MOBILE.md) - iOS remote setup, server connections, and notifications

- [../README.md](../README.md) — product overview, feature summary, install/run and build-from-source instructions
- [index.html](index.html) — public GitHub Pages launch page for Controllarr for Windows
- [DESKTOP.md](DESKTOP.md) — desktop controls, bulk actions, taskbar settings, and testing instructions
- [NATIVE_DESKTOP_VALIDATION.md](NATIVE_DESKTOP_VALIDATION.md) - release test results and performance measurements
- [INSTALL.md](INSTALL.md) - x64/ARM64 installers, profile preservation/import, portable ZIPs and package builds
- [STORAGE.md](STORAGE.md) - per-torrent category folders, trailing separators, completed moves, archive scope and existing-data safety
- [NETWORKING.md](NETWORKING.md) - enforced adapter binding, LAN separation, SOCKS5/DPAPI, DNS, blocklists and advanced-client limitations
- [OPERATIONS.md](OPERATIONS.md) — headless/always-on usage, backup/export/restore, recovery rules, post-processing retries, disk-space operations, VPN diagnostics, and the on-disk log
- [PERFORMANCE.md](PERFORMANCE.md) — large-library scaling notes, the 2s polling model, and tuning guidance for high torrent counts with MonoTorrent
- [STALL_AUDIT.md](STALL_AUDIT.md) - tracker, DNS, DHT, and stalled-download troubleshooting
- [V1_5_ROADMAP.md](V1_5_ROADMAP.md) — historical development roadmap

## Releases

- [GitHub Releases](https://github.com/eMacTh3Creator/Controllarr-Windows/releases) — x64/ARM64 installers, portable ZIPs, checksums and per-version notes
- [Release Notes](../RELEASE_NOTES_v2.3.0.md) - clean torrent folders, safe moves, scoped extraction and storage persistence

**Version 2.3.0** fixes Sonarr import paths and adds a tool to repair older folder layouts after confirmation. It also supports the iOS remote app and optional discovery on your LAN. Installers are available for x64 and ARM64. See the release notes for build and testing details.

## What Lives Where

- Use the top-level `README.md` for the overview and setup instructions.
- Use `OPERATIONS.md` for instructions for running and maintaining the app.
- Use `V1_5_ROADMAP.md` for forward-looking release planning and large feature themes; treat it as historical product-direction context rather than a commitment.
- Keep the GitHub Releases notes historical: each one should describe what shipped in that release, not the future roadmap.
