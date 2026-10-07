# v2.2.1 - Faster bulk removal and Windows installers

## Bulk Removal

- Pause the entire confirmed selection first; queue and force-start cannot revive pending targets.
- Up to 16 stop workers overlap, with two-second final tracker-announce waits instead of sequential indefinite waits. Disk flushes still finish before removal.
- Disk deletion remains serial, with phased progress, cancellation and per-item failures. Cancelled or failed targets remain paused after restart.
- Lighter removal checkpoints avoid repeatedly rewriting resume files for the remaining library. API deletion and category saves are batched too; `hashes=all` works.

## Installers and Profiles

- Full self-contained x64 and ARM64 Setup EXEs, Start menu entry, optional desktop shortcut and uninstaller. No separate .NET install; portable ZIPs remain available.
- Existing AppData settings/library are kept by default, including upgrades from portable builds.
- Optional previous-profile import copies settings/categories, resume metadata, layout and RSS history, backs up target metadata, and leaves source/downloaded files untouched.
- Optional same-user DPAPI credential import. If skipped, WebUI defaults to loopback until credentials and LAN access are configured again.
- Cached metadata paths are rebased before engine restore; payload paths stay unchanged.
- Stale removed cache entries in a pre-deletion checkpoint no longer prevent restoring the remaining torrent library.
- Stable per-user install identity supports later installer upgrades. Built-in automatic installation is not implemented yet.

## Installation and Limits

Finish or cancel the current removal batch, then **Exit** the old app from its tray.
Run the installer matching your CPU and choose **Keep existing settings** for a
normal upgrade. See [INSTALL.md](docs/INSTALL.md).

Installers/app binaries remain unsigned; SmartScreen can warn. Verify
SHA256SUMS.txt; do not disable Windows security globally. ARM64 remains
experimental after an earlier single-file API AccessViolation; short folder-build
checks do not establish its root cause or long-term stability. x64 VM tests use
ARM emulation, not physical Intel/AMD hardware. Real-provider VPN packet capture
and multi-day production load remain outside these prechecks.

Controlled VM benchmark: 2,376 paused 16 KiB local fixture torrents and their files
removed in 37.37 seconds. No peers/trackers were present in that benchmark;
real storage, active peers and large payloads can differ.
