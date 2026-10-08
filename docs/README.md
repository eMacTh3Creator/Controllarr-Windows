# Controllarr for Windows Docs

This folder holds the higher-level product and planning docs that sit alongside the source tree for the Windows port.

## Start Here

- [MOBILE.md](MOBILE.md) - iOS preview setup, authenticated remote APIs, discovery and notification limits

- [../README.md](../README.md) — product overview, feature summary, install/run and build-from-source instructions
- [index.html](index.html) — public GitHub Pages launch page for Controllarr for Windows
- [DESKTOP.md](DESKTOP.md) — native desktop controls, bulk selection/deletion, taskbar behavior, isolated testing and known limits
- [NATIVE_DESKTOP_VALIDATION.md](NATIVE_DESKTOP_VALIDATION.md) - VM test results, performance measurements and remaining validation limits
- [INSTALL.md](INSTALL.md) - x64/ARM64 installers, profile preservation/import, portable ZIPs and package builds
- [STORAGE.md](STORAGE.md) - per-torrent category folders, trailing separators, completed moves, archive scope and existing-data safety
- [NETWORKING.md](NETWORKING.md) - enforced adapter binding, LAN separation, SOCKS5/DPAPI, DNS, blocklists and advanced-client limitations
- [OPERATIONS.md](OPERATIONS.md) — headless/always-on usage, backup/export/restore, recovery rules, post-processing retries, disk-space operations, VPN diagnostics, and the on-disk log
- [PERFORMANCE.md](PERFORMANCE.md) — large-library scaling notes, the 2s polling model, and tuning guidance for high torrent counts with MonoTorrent
- [STALL_AUDIT.md](STALL_AUDIT.md) - shipped metadata/tracker/DNS/DHT stall repairs, evidence and remaining optimization work
- [V1_5_ROADMAP.md](V1_5_ROADMAP.md) — historical product-direction roadmap for a more ambitious release, adapted from the macOS roadmap

## Releases

- [GitHub Releases](https://github.com/eMacTh3Creator/Controllarr-Windows/releases) — x64/ARM64 installers, portable ZIPs, checksums and per-version notes
- [Release Notes](../RELEASE_NOTES_v2.3.0.md) - clean torrent folders, safe moves, scoped extraction and storage persistence

The current Windows release is **v2.3.0**, correcting Sonarr import paths and adding confirmed layout repair, authenticated Remote Protocol 1, bounded events and optional LAN discovery. Existing files are not automatically reorganized. Both CPU builds retain bound DHT, native desktop controls, profile-preserving installers, live queue/peer-budget diagnosis and pause-first removal. ARM64 is experimental. The native iOS preview has an Apple-signed build active in internal TestFlight testing; reliable APNs background push and public beta access remain unfinished. Windows uses MonoTorrent and is not behaviorally identical to the Mac engine. Built-in automatic installation is still planned.

## What Lives Where

- Use the top-level `README.md` for the current product story and setup instructions.
- Use `OPERATIONS.md` for operator-facing runtime workflows that are more detailed than the top-level README.
- Use `V1_5_ROADMAP.md` for forward-looking release planning and large feature themes; treat it as historical product-direction context rather than a commitment.
- Keep the GitHub Releases notes historical: each one should describe what shipped in that release, not the future roadmap.
