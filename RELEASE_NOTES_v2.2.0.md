# v2.2.0 - Native Windows desktop and torrent networking

Controllarr for Windows now has a standalone WPF transfer workspace, not a
WebUI wrapper, with separate self-contained Intel/AMD x64 and Windows ARM64
downloads. **ARM64 remains experimental**; see validation limits below.

## Desktop and Client Controls

- Virtualized torrent rows, category/status/search filters, persistent column
  layouts, multi-selection and stable selection across polling updates.
- Bulk pause/resume, category assignment, storage moves, verification,
  reannounce and removal. Right-click or Delete opens a dialog: keep downloaded
  files by default, or permanently delete them with an additional confirmation.
- Native menus, keyboard shortcuts, tray controls, taskbar progress and
  pause/resume thumbnail buttons. Closing to tray is configurable.
- Managed queues and force-start controls; global/per-torrent speed limits,
  connection budgets, upload slots, file priorities and sequential downloading.
- Files/Trackers/Peers inspector and temporary loopback-only streaming previews
  with bounded buffers and seek-aware picking.
- RSS/Atom rules, watch-folder intake, background torrent creation and paused
  metadata migration from uTorrent/BitTorrent or qBittorrent backups.

## Torrent Networking and Reliability

- Enforced IPv4 VPN adapter source/interface binding for peers, trackers,
  webseeds and DNS, with no ordinary-interface fallback. Missing adapters block
  add/resume/force-start/queue starts and close existing torrent sockets.
- The LAN WebUI/qBittorrent-compatible API stays separate from torrent binding.
  VPN-provider LAN policy and Windows firewall rules still apply; the app cannot
  bypass them. Keep the provider's kill switch enabled as defense in depth.
- SOCKS5 TCP proxy with remote hostname resolution, optional authentication and
  no direct fallback; Prefer/Require/Disable peer-encryption controls; local
  IPv4/IPv6 CIDR blocklists. Settings topology changes require a restart.
- Serialized engine settings prevent overlapping rate/port updates from
  overwriting encryption settings. Atomic, serialized resume checkpoints avoid
  concurrent library startup/state writes and restore verified progress.
- Cached engine snapshots, incremental desktop updates and bounded inspection,
  preview, feed and blocklist work reduce avoidable large-library overhead.
- WebUI/SOCKS5 passwords use current-user Windows DPAPI, without per-access
  password prompts. Exported configuration backups include plaintext secrets;
  store them securely and never attach them to public issues.

## Install or Upgrade

Choose the matching ZIP:

- [Intel/AMD x64](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.0/Controllarr-2.2.0-win-x64.zip)
- [Windows ARM64 - experimental](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.0/Controllarr-2.2.0-win-arm64.zip)
- [SHA256SUMS.txt](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.0/SHA256SUMS.txt)

Back up `%AppData%\Controllarr`, exit the old app, and extract **every file** into
a new application folder. Run `Controllarr.exe` from that folder. No separate
.NET/Edge/WebView2 install is required. Do not copy only the EXE or mix old/new
runtime DLLs; update shortcuts to the new folder. The profile remains separate.

The app is unsigned. Download only from this project and compare the ZIP's
`Get-FileHash -Algorithm SHA256` output to the published checksum. If Windows
SmartScreen warns, only use **More info > Run anyway** for a trusted, verified
download. Do not disable Defender or the firewall globally. A checksum checks
integrity, not publisher identity. macOS self-signing commands do not apply.

For remote Sonarr/Radarr, configure the LAN bind host, change default
`admin` / `adminadmin` credentials, restart, and permit only the intended LAN
clients. Seerr/Overseerr uses Sonarr/Radarr rather than connecting as a download
client itself. See the [install guide](https://github.com/eMacTh3Creator/Controllarr-Windows/blob/master/docs/INSTALL.md)
and [networking guide](https://github.com/eMacTh3Creator/Controllarr-Windows/blob/master/docs/NETWORKING.md).

## Prechecks and Known Limits

Release builds, desktop logic/real-engine checks and the WPF harness run before
packaging both architectures. Isolated VM tests cover authenticated API access,
restart/paused-torrent persistence and fail-closed missing-adapter behavior.
The WPF harness realizes 13 rows for 10,000 items; a separate 1,000-tiny-torrent
benchmark is not a real-network throughput or whole-desktop memory claim.

Earlier ARM64 single-file builds reproduced an ASP.NET API AccessViolation.
Complete-folder builds passed short restart/poll checks, but the root cause is
unproven. x64 was exercised under Windows-on-ARM emulation, not physical Intel/
AMD hardware. Real-provider packet-capture/drop/reconnect testing and multi-day
mixed-library load remain uncompleted. Publication is based on prechecks, not
a promise of perfect load behavior or exhaustive client parity.

Protected modes deliberately disable DHT, local peer discovery, router mapping
and IPv6 torrent fallback; use tracker-backed torrents and forwarded ports.
SOCKS5 UDP ASSOCIATE, super-seeding, full foreign verified-piece resume
conversion and remote blocklist subscriptions are not implemented. The WebUI
does not expose every new desktop-only creation/migration/queue workflow.
See [validation details](https://github.com/eMacTh3Creator/Controllarr-Windows/blob/master/docs/NATIVE_DESKTOP_VALIDATION.md).
