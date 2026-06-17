# v2.1.15 — Windows reaches 1:1 parity with macOS

This release brings Controllarr for Windows fully in line with macOS Controllarr
v2.1.15. The Windows port now matches the Mac build feature-for-feature, ships a
bundled browser Web UI, and gains the same persistent crash-surviving log that
landed on macOS — plus a few Windows-native touches.

## 1:1 alignment with macOS v2.1.15

Every service that ships in macOS Controllarr v2.1.15 now has a Windows
counterpart: automatic listen-port cycling, preferred-forwarded-port handling
for VPN providers, the VPN kill switch with interface binding, disk-space-aware
auto-pause, *arr re-search integration, the recovery center with rule chaining,
post-processing (move + extract), seeding policy with hit-and-run protection,
the bandwidth scheduler, the health monitor, and backup export/restore. The
version number is intentionally matched so the two ports track each other.

## Added

- **Bundled no-build Web UI.** A static SPA is now served by the embedded
  server at <http://127.0.0.1:8791> — no separate install, no build step. It
  uses a dark Windows 11-style theme and exposes Home, Torrents, Categories,
  Settings, Health, Recovery, Post-Processor, Seeding, and Log tabs, with
  per-torrent Files / Trackers / Peers detail. Default login is
  `admin` / `adminadmin`. The WebUI assets ship beside `Controllarr.exe`.
- **Persistent log file that survives crashes.** Controllarr now mirrors its
  runtime log to disk at `%AppData%\Controllarr\logs\controllarr.log`,
  flushed frequently (and immediately on warnings/errors, plus every few lines)
  so it survives an app crash or a full machine reboot. It rotates at ~5 MB and
  keeps one previous file (`controllarr.log.1`). Previously the log lived only
  in memory and was lost when the app or the PC went down — which is exactly
  when you need it.
- **Reveal Log File button** in the Log tab (and the native Log view) opens that
  file in File Explorer, so you can grab it after a crash without touching the
  command line.
- **Native Home dashboard.** A new default tab in the WPF app with session
  metric cards, connection/VPN/disk status pills, quick actions, and a
  most-active-transfers list — a glance-able overview before you dive into the
  torrent list.
- **Torrents search box.** Filter the torrent list by name, category, or
  info-hash, with a one-click clear.
- **GitHub-release update check.** Replacing the macOS Sparkle updater, Windows
  gets a "Check for Updates" action plus a settings toggle. It queries the
  GitHub Releases API and opens the latest release page in your browser rather
  than silently installing anything — you stay in control of the upgrade.

## Security

- **WebUI hardening.** The embedded server now sets security headers
  (`X-Content-Type-Options`, `Referrer-Policy`), supports optional clickjacking
  protection (`X-Frame-Options` / `Content-Security-Policy`), and offers an
  optional IP allowlist (CIDR) so you can restrict who may reach the API/Web UI.
  Session auth uses a 1-hour sliding expiry, with CORS support for cross-origin
  *arr setups.
- **DPAPI credential wiring.** The WebUI/API password is encrypted at rest with
  the Windows Data Protection API (DPAPI) in `credentials.dat`, bound to the
  current Windows user account; *arr API keys remain in `state.json`.

## Packaging

- **Self-contained single-file `.exe`.** Distribution is a single
  `Controllarr.exe` (win-x64) published self-contained — no .NET runtime
  installation required. Target framework stays on `net8.0-windows` (LTS). The
  bundled Web UI assets ship beside the executable.

## Upgrading / running

The `.exe` is unsigned, so the first launch may be flagged:

- **Windows SmartScreen** may show a "Windows protected your PC" prompt — click
  **More info → Run anyway**.
- Alternatively, clear the mark-of-the-web before launching: right-click
  `Controllarr.exe` → **Properties** → check **Unblock** → **OK**, or run
  `Unblock-File .\Controllarr.exe` in PowerShell.

Your existing settings in `%AppData%\Controllarr\state.json` carry forward; no
reconfiguration is needed. After upgrading, point Sonarr / Radarr / Overseerr at
`http://<host>:8791` using the qBittorrent download client type as before.

Carries forward all prior Windows port functionality and the macOS v2.1.14
large-library networking mitigations.
