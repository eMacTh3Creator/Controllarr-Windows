# Controllarr for Windows Docs

This folder holds the higher-level product and planning docs that sit alongside the source tree for the Windows port.

## Start Here

- [../README.md](../README.md) — product overview, feature summary, install/run and build-from-source instructions
- [index.html](index.html) — public GitHub Pages launch page for Controllarr for Windows
- [OPERATIONS.md](OPERATIONS.md) — headless/always-on usage, backup/export/restore, recovery rules, post-processing retries, disk-space operations, VPN diagnostics, and the on-disk log
- [PERFORMANCE.md](PERFORMANCE.md) — large-library scaling notes, the 2s polling model, and tuning guidance for high torrent counts with MonoTorrent
- [V1_5_ROADMAP.md](V1_5_ROADMAP.md) — historical product-direction roadmap for a more ambitious release, adapted from the macOS roadmap

## Releases

- [GitHub Releases](https://github.com/eMacTh3Creator/Controllarr-Windows/releases) — pre-built, self-contained `Controllarr.exe` (win-x64) downloads and per-version notes

The current shipping build is **Controllarr for Windows v2.1.15**, which aligns the Windows port 1:1 with macOS Controllarr v2.1.15. Use the GitHub Releases page above for the changelog of each version; release notes are published there per tag rather than as files checked into this folder.

## What Lives Where

- Use the top-level `README.md` for the current product story and setup instructions.
- Use `OPERATIONS.md` for operator-facing runtime workflows that are more detailed than the top-level README.
- Use `V1_5_ROADMAP.md` for forward-looking release planning and large feature themes; treat it as historical product-direction context rather than a commitment.
- Keep the GitHub Releases notes historical: each one should describe what shipped in that release, not the future roadmap.
