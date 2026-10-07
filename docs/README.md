# Controllarr for Windows Docs

This folder holds the higher-level product and planning docs that sit alongside the source tree for the Windows port.

## Start Here

- [../README.md](../README.md) — product overview, feature summary, install/run and build-from-source instructions
- [index.html](index.html) — public GitHub Pages launch page for Controllarr for Windows
- [DESKTOP.md](DESKTOP.md) — native desktop controls, bulk selection/deletion, taskbar behavior, isolated testing and known limits
- [NATIVE_DESKTOP_VALIDATION.md](NATIVE_DESKTOP_VALIDATION.md) - VM test results, performance measurements and remaining validation limits
- [INSTALL.md](INSTALL.md) - x64 versus ARM64, complete-folder ZIP installation and building both packages
- [NETWORKING.md](NETWORKING.md) - enforced adapter binding, LAN separation, SOCKS5/DPAPI, DNS, blocklists and advanced-client limitations
- [OPERATIONS.md](OPERATIONS.md) — headless/always-on usage, backup/export/restore, recovery rules, post-processing retries, disk-space operations, VPN diagnostics, and the on-disk log
- [PERFORMANCE.md](PERFORMANCE.md) — large-library scaling notes, the 2s polling model, and tuning guidance for high torrent counts with MonoTorrent
- [V1_5_ROADMAP.md](V1_5_ROADMAP.md) — historical product-direction roadmap for a more ambitious release, adapted from the macOS roadmap

## Releases

- [GitHub Releases](https://github.com/eMacTh3Creator/Controllarr-Windows/releases) — self-contained x64/ARM64 ZIP downloads, checksums and per-version notes
- [Release Notes](../RELEASE_NOTES_v2.2.0.md) - native desktop and torrent-networking release changes, installation and validation limits

The current Windows release is **v2.2.0**, with x64/ARM64 folder packages, managed queues, torrent creation/migration, RSS/watch folders, enforced VPN socket binding, SOCKS5/encryption/blocklist controls and sequential/streaming support. ARM64 is experimental. Windows uses MonoTorrent and is not behaviorally identical to the macOS engine.

## What Lives Where

- Use the top-level `README.md` for the current product story and setup instructions.
- Use `OPERATIONS.md` for operator-facing runtime workflows that are more detailed than the top-level README.
- Use `V1_5_ROADMAP.md` for forward-looking release planning and large feature themes; treat it as historical product-direction context rather than a commitment.
- Keep the GitHub Releases notes historical: each one should describe what shipped in that release, not the future roadmap.
