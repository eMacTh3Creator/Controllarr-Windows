# Operations Guide

This doc covers the operator-focused foundations available in Controllarr for
Windows v2.1.15. This release aligns the Windows port 1:1 with macOS Controllarr
v2.1.15, so the operator workflows below mirror the macOS guide while reflecting
the Windows engine, paths, and tooling.

## Performance and Scale

For large libraries, Controllarr changes the way it gathers and republishes
state:

- torrent/session snapshots are cached briefly inside the engine so the runtime,
  native WPF UI, bundled Web UI, and API can reuse one MonoTorrent scan instead
  of forcing several back-to-back scans
- the runtime fans post-processing, seeding policy, and health analysis out in
  parallel after a shared torrent snapshot is collected
- the native app splits fast-changing state from admin state, so
  torrents/session/health stay fresh while categories, logs, and recovery
  history refresh on a slower cadence
- the bundled Web UI refreshes the active tab instead of pulling every table on
  every 2-second interval

If you are running especially large libraries:

- prefer the published self-contained `Controllarr.exe` (Release build) over a
  Debug build for any real load testing
- keep the native window closed and run from the system tray for always-on nodes
  that do not need the dashboard open all day
- keep the Log tab and detailed tracker/peer views for active troubleshooting
  instead of leaving them open permanently
- validate your own environment with Task Manager or Resource Monitor,
  especially if your torrents live on slower external storage or
  network-mounted/SMB volumes

Controllarr is tuned to scale well, but real-world performance still depends on
the mix of active peers, trackers, disk speed, and the number of simultaneously
changing torrents. See [PERFORMANCE.md](PERFORMANCE.md) for the engine-level
detail.

## Running Controllarr

Controllarr for Windows ships as a single self-contained `Controllarr.exe`
(win-x64). No .NET install is required.

- Launch `Controllarr.exe` to start the WPF app, the embedded Kestrel HTTP
  server, and the bundled Web UI together.
- The app runs as a single instance with a system tray icon that shows the
  current listen port, torrent count, and transfer rates.
- Closing the window keeps Controllarr running in the tray so the HTTP API,
  Web UI, and torrent engine stay available. Use **Exit** from the tray menu to
  fully shut down; this flushes the current listen port and state cleanly.
- Optionally enable **launch at startup** so Controllarr starts with Windows.

### First-Run / SmartScreen

The `.exe` is unsigned. On first run Windows SmartScreen may show a warning;
choose **More info -> Run anyway**. Alternatively, clear the mark-of-the-web
before launching: right-click `Controllarr.exe` -> **Properties** -> tick
**Unblock**, or run `Unblock-File .\Controllarr.exe` in PowerShell.

### LAN Access

For LAN access from another machine, bind the Web UI to `0.0.0.0`, restart
Controllarr so the HTTP server rebinds, and connect to the machine's LAN IP
instead of `127.0.0.1`. See **Remote Access and VPN Diagnostics** below.

## Remote Access and VPN Diagnostics

Controllarr includes a built-in network diagnostics panel in both the native
Settings view and the Web UI Settings tab.

### What It Shows

- the current Web UI/API bind host and port
- the local URL the host machine itself should open
- detected private LAN IPs on the machine
- the currently detected VPN interface and IP
- whether torrent traffic is bound to the VPN adapter
- a recommended LAN URL for Sonarr, Radarr, Overseerr, or another browser on
  your network

### How To Use It

- Set **Settings -> Web UI -> Bind host** (`webui_host`) to `0.0.0.0` to listen
  on all interfaces, or to a specific LAN IP such as `192.168.1.122`
- Save settings and restart Controllarr so the HTTP server rebinds
- Point remote clients at the recommended LAN URL (the machine's LAN IP plus the
  configured `webui_port`, e.g. `http://192.168.1.122:8791`), not `127.0.0.1`
  and not `0.0.0.0`

### VPN Caveat

- Controllarr keeps torrent traffic on the VPN adapter separately from the
  Web UI/API listener, so binding the Web UI to `0.0.0.0` does not push the Web
  UI through the VPN
- If diagnostics say remote access is configured correctly but other machines
  still cannot connect while the VPN is on, the VPN client is likely blocking
  inbound LAN traffic. Check the VPN client's LAN-allow/local-network setting and
  the Windows Defender Firewall inbound rule for `Controllarr.exe`

### Web UI Hardening for Remote Access

When you expose the Web UI beyond `127.0.0.1`, tighten access from
**Settings -> Web UI**:

- session auth is always on (1-hour sliding expiry); change the default
  `admin` / `adminadmin` credentials before exposing the host
- enable the optional IP allowlist (CIDR) to restrict which clients can reach
  the Web UI/API
- enable optional clickjacking protection (`X-Frame-Options` / CSP) if the Web
  UI will be embedded or proxied; baseline security headers
  (`X-Content-Type-Options`, `Referrer-Policy`) are always sent

## Preferred Forwarded Port

Some VPN providers, including PIA, assign a specific forwarded incoming port.
Set **Settings -> General -> Preferred forwarded port** to that value, for
example `53127`.

Controllarr uses this port before the last-known-good port on startup, applies
it immediately when saved, and tries it before random fallback ports during port
cycling. If the preferred port itself goes stale, Controllarr moves to a
fallback from the configured range (`listen_port_range_start` to
`listen_port_range_end`, defaults `49152`-`65000`); if a fallback later goes
stale, it retries the preferred port.

Leave the field blank if your VPN does not provide a forwarded port or if the
port changes too often to manage manually.

## VPN Adapter Binding

Controllarr detects VPN adapters by scanning Windows network interfaces with
`NetworkInterface` for adapters matching:

- `TAP-Windows` / `TAP-Win32` (OpenVPN)
- `WireGuard`
- `Wintun` (the WireGuard kernel driver)
- any adapter whose name starts with the configured `vpn_interface_prefix`
  (default `TAP`)

When VPN is enabled:

1. **VPN connected** — MonoTorrent's outgoing and listen interfaces are bound to
   the VPN adapter IP, so torrent traffic never leaks through the default route
2. **VPN disconnected + kill switch on** — all active torrents are instantly
   paused
3. **VPN reconnects** — paused torrents automatically resume

If your VPN client uses an adapter name that does not match the built-in
patterns, set `vpn_interface_prefix` to that adapter's name prefix so detection
and binding work correctly.

## Automatic Updates (GitHub Releases)

Controllarr for Windows checks for updates through the GitHub Releases API. This
replaces the Sparkle update framework used by the macOS app.

- A **Check for Updates** action is available in the native app, and a settings
  toggle controls automatic checking.
- When a newer release is found, Controllarr opens the latest release page in
  your browser rather than silently installing the update.
- Updating is a manual download-and-replace: close Controllarr, replace the
  existing `Controllarr.exe` with the new one, and relaunch. Your settings in
  `%AppData%\Controllarr\state.json` are preserved across the swap.
- Because the `.exe` is unsigned, re-apply the SmartScreen / **Unblock** step
  from **First-Run / SmartScreen** above the first time you run a freshly
  downloaded build.

## Backup, Export, and Restore

The Web UI Settings tab includes a **Backup & restore** panel (also exposed via
`GET/POST /api/controllarr/backup`).

### Export

- Use **Export backup** to download the current Controllarr state as JSON.
- Turn on **Include saved secrets in exports** if you want the backup to carry
  the Web UI password and saved *arr API keys.
- A redacted export is still useful for categories, save-path routing, seeding
  policy, health settings, and other non-secret state.
- Only the Web UI / API password is encrypted at rest via Windows DPAPI (in
  `credentials.dat`); *arr API keys are stored in the app-state file
  (`state.json`). DPAPI keys are tied to the user account on the original
  machine, so the encrypted Web UI password in a secrets-included backup is only
  directly portable on the same Windows user profile. Moving to a different user
  or machine means re-entering the Web UI password after import.

### Import

- Choose a previously exported JSON file and click **Import backup**.
- Import replaces the current persisted settings and categories with the backup
  contents.
- If the imported backup changes the Web UI bind host or port, Controllarr warns
  that a restart is recommended.

### Current Limitations

- A backup exported without secrets is not a full machine-to-machine credential
  migration.
- Host and port changes are restored into persisted settings, but the running
  Kestrel HTTP server keeps its existing bind until restart.
- Backup/restore is focused on persisted operator state; it is not a full
  historical analytics export.

## Recovery Rules and Recovery Center

Controllarr includes a health-based recovery engine surfaced in the Recovery
tab.

### What It Can Do

- Match active health issues such as metadata timeouts, no-peer stalls, stalled
  torrents with peers, and awaiting-recheck states
- Apply one automatic action per configured reason:
  - `reannounce`
  - `pause`
  - `remove_keep_files`
  - `remove_delete_files`
- Keep a rolling log of both automatic and manual recovery attempts in the
  Recovery tab

### How To Configure It

- Open the Settings tab and scroll to **Recovery rules**
- Add one or more rules with:
  - health reason
  - action
  - delay in minutes
  - enabled/disabled state
- Only the first enabled rule for a given reason is applied automatically

### Manual Recovery

- The Health tab includes **Recover now** for active issues
- Manual recovery uses the configured rule for that health reason when one exists
- If no rule exists yet, Controllarr falls back to a one-off `reannounce`

### Current Limitations

- Recovery rules currently focus on health issues only; they do not yet react to
  disk pressure, import failures, or tracker-specific policies
- Automatic runs are de-duplicated while the same issue remains active, then
  become eligible again if the issue clears and later returns

## Post-Processor Retry Queue

The Web UI Post-Processor tab is interactive, not read-only.

### What It Can Do

- Show move and extraction (SharpCompress) records for completed torrents
- Flag failed records as retryable
- Queue a manual retry that re-enters the post-processing pipeline for that
  torrent

### How To Use It

- Open the **Post-Processor** tab
- Find a row with a failed stage
- Click **Retry** to move the record back to `pending` and immediately
  re-evaluate the torrent

### Current Limitations

- Retry targets failed records only; successful rows are informational
- The retry path reuses the current category settings, so changed save paths or
  extraction toggles affect the next run
- If the torrent is no longer loaded in the session, the retry request is
  rejected

## Disk Space Monitor Operations

The Settings tab exposes a disk-space status card (also at
`GET /api/controllarr/diskspace`).

### What It Shows

- The path currently being monitored
- Current free space and the active threshold (`disk_space_minimum_gb`)
- Whether downloads are paused by the monitor
- Which torrent hashes were paused by the monitor
- How much space is still needed before downloads can safely resume

### Operator Recheck

- Use **Recheck now** after freeing space or changing the configured threshold
- The recheck triggers an immediate monitor evaluation instead of waiting for the
  normal 30-second loop
- If free space is back above threshold, Controllarr resumes the torrents it
  paused

## Seeding Policy

The Seeding tab logs ratio/time enforcement actions.

- Set a global **max ratio** (`global_max_ratio`) and/or **max seeding time**
  (`global_max_seeding_time_minutes`); categories can override these per category.
- When a torrent crosses a limit, Controllarr applies the configured
  `seed_limit_action`: `pause`, `remove_keep_files`, or `remove_delete_files`.
- **Hit-and-run protection** keeps a torrent seeding until at least
  `minimum_seed_time_minutes` (default 60) has elapsed, even if a ratio limit is
  already met, so private-tracker requirements are respected.
- The Seeding tab shows which torrents were acted on, the trigger (ratio or
  time), and the action taken.

## Bandwidth Scheduler

The bandwidth scheduler applies time-of-day download/upload rate limits.

- Configure rules from **Settings -> Bandwidth** with a start/end time and the
  download and upload caps that apply during that window.
- The scheduler self-polls on its own cadence and applies the active rule to the
  MonoTorrent engine; outside any scheduled window the global/unthrottled limits
  apply.
- Rate limits set by the scheduler are reflected in the qBittorrent
  `transfer/speedLimitsMode` surface that *arr apps read.

## Persistent Log and Reveal Log File

Controllarr writes a persistent, crash-surviving log to disk in addition to the
in-app Log tab:

- Location: `%AppData%\Controllarr\logs\controllarr.log`
- The log is fsync'd on warn/error and every few lines, so recent activity
  survives an unexpected crash or power loss.
- It rotates at roughly 5 MB, keeping a single `.1` backup
  (`controllarr.log.1`).
- The in-app **Log** tab provides a live, filterable view with level coloring
  (Debug/Info/Warn/Error); the API exposes it at
  `GET /api/controllarr/log?limit=...`.
- Use the **Reveal Log File** action to open the log's folder in File Explorer
  with the file selected — useful when attaching logs to a bug report or
  inspecting history beyond what the in-app buffer holds.

## State File

All persisted operator settings and categories live in
`%AppData%\Controllarr\state.json` (this includes the *arr API keys). Writes are
debounced. When migrating a host, copy this file to carry over your
configuration, including the *arr API keys. Only the DPAPI-encrypted Web UI
password (in `credentials.dat`) does not transfer to a different Windows user
profile — it resets to the default `adminadmin` and must be re-entered (or use a
secrets-included backup on the same user profile).
