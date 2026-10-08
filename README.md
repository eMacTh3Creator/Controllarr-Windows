<p align="center">
  <img src="docs/assets/icon-256.png" alt="Controllarr icon" width="160" height="160" />
</p>

<h1 align="center">Controllarr for Windows</h1>

<p align="center">A native Windows BitTorrent client built for Sonarr / Radarr / Overseerr / Plex workflows.</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%2F11-blue" alt="Windows 10/11" />
  <img src="https://img.shields.io/badge/.NET_8-net8.0--windows-purple" alt=".NET 8" />
  <img src="https://img.shields.io/badge/x64-supported-green" alt="x64" />
  <img src="https://img.shields.io/badge/ARM64-experimental-orange" alt="ARM64 experimental" />
  <img src="https://img.shields.io/badge/license-MIT-green" alt="MIT License" />
</p>

---

Controllarr for Windows is the Windows counterpart to [macOS Controllarr](https://github.com/eMacTh3Creator/Controllarr). It uses [MonoTorrent](https://github.com/alanmcgovern/monotorrent) inside a native WPF desktop app. Sonarr and Radarr connect using their qBittorrent download-client configuration; remote machines also need a reachable LAN bind address and appropriate firewall/VPN settings.

**Current release:** [v2.2.4](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/tag/v2.2.4) adds per-torrent subfolder checkboxes, preserves multi-file folders during completed/manual moves, rejects destination collisions and scopes archive extraction to the torrent's own files. **A trailing backslash is not required.** Existing profiles and payload paths are retained; enable the checkbox in each existing category for future downloads. It retains VPN-bound DHT, discovery repairs, live queues and profile-preserving x64/ARM64 installers. The desktop is native WPF, not an embedded WebUI portal. See the [project website](https://emacth3creator.github.io/Controllarr-Windows/), [storage guide](docs/STORAGE.md), [desktop guide](docs/DESKTOP.md), [networking guide](docs/NETWORKING.md) and [VM validation report](docs/NATIVE_DESKTOP_VALIDATION.md). Windows and macOS use different torrent engines.

**Known limits:** both CPU builds use self-contained app folders. ARM64 remains experimental: earlier single-file packages reproduced an intermittent API AccessViolation whose root cause is not established. Folder builds passed the listed pre-checks. Real-provider VPN leak testing, physical x64 hardware validation and long-duration load testing remain incomplete. Back up your profile before updating; see the validation report for exact scope.

**Reliability audit:** the [torrent stall audit](docs/STALL_AUDIT.md) documents shipped tracker failover/refresh, VPN DNS, metadata health/recovery, selected-file queue completion and intake repairs. Remaining work includes synchronous post-processing/*arr I/O, metadata-slot rotation, download fairness and real-provider VPN validation.

## Download

| Platform | Download | Requirements |
|----------|----------|--------------|
| **Windows x64** | [Download Setup.exe directly](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.4/Controllarr-2.2.4-win-x64-Setup.exe) | Intel/AMD Windows 10/11, including x64 Plexboxes |
| **Windows ARM64** | [Download Setup.exe directly](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.4/Controllarr-2.2.4-win-arm64-Setup.exe) | Windows 11 on ARM; experimental |

Exit the old app from its tray, then run the matching installer. **Keep existing settings** reuses the AppData profile; optional import restores a previous profile folder with a metadata backup. Downloaded files are not moved or deleted by installation. No separate .NET, Edge or WebView2 is required. Portable ZIPs and [SHA256SUMS.txt](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.4/SHA256SUMS.txt) are also available. Binaries remain unsigned. These are Windows builds, not macOS binaries. See [the installation guide](docs/INSTALL.md). Built-in automatic installation is still planned.

On first launch, the Web UI is available at <http://127.0.0.1:8791> — default login is `admin` / `adminadmin`. Point Sonarr / Radarr at the same URL using the qBittorrent download client type.

---

## Features

- **Automatic listen-port reselection** when the forwarded port goes offline (the #1 reason this project exists)
- **qBittorrent Web API v2** compatibility — Sonarr / Radarr connect directly; Seerr/Overseerr works through those apps
- **Bundled browser Web UI** — a no-build static SPA served by the embedded server at <http://127.0.0.1:8791> with a dark Windows 11-style theme, login, and tabs for Home / Torrents / Categories / Settings / Health / Recovery / Post-Processor / Seeding / Log (with per-torrent Files / Trackers / Peers detail)
- **Standalone native desktop** — no Edge/WebView2 dependency or browser login required for local control; the remote WebUI/API remains available separately
- **Large-library transfer workspace** — virtualized rows and columns, stable row identity, debounced search, sortable columns, category navigation and combined status/category/search filters
- **Bulk torrent management** — Ctrl/Shift selection, select all shown, pause/resume, reannounce, verify files, assign category, move storage, copy magnets and open folders
- **Safe removal** — right-click or press Delete to choose registration-only removal or permanent file deletion; keep files is the default, and permanent deletion requires another confirmation
- **Native menus, toolbar and taskbar** — File/Edit/Transfers/View/Tools/Help, shortcuts, aggregate download progress, taskbar thumbnail pause/resume and a working tray menu
- **Native Home dashboard** — session metric cards, status pills, quick actions, and a most-active-transfers list; Transfers is now the default workspace
- **Graphite and teal desktop theme** — compact Windows controls, keyboard navigation and resizable transfer/details panes
- **Per-torrent detail** — persistent file priorities, editable public-torrent trackers and refreshable peer snapshots
- **Aware active queue** - contiguous live positions after removal/restart, download-first admission under total caps, separate download/seed limits and explicit force-start; queued state and waiting reasons are reflected in the API
- **Download connection headroom** - configurable reserve (25% by default, 0 disables it); ordinary seeds share the remaining peer budget while downloads need connections, without raising the global limit
- **Transfer diagnosis** - global used/limit counter and selected-torrent reasons for queue caps, VPN/disk guards, metadata waits, peer-budget exhaustion and connected-but-idle peers; unavailable tracker scrape counts display Unknown, not zero seeders
- **Speed controls** — global and multi-selected per-torrent limits, with scheduled limits falling back to the global values
- **Advanced torrent controls** - bulk connection budgets, upload slots and persistent sequential piece selection; settings supply per-torrent defaults and PEX policy
- **File streaming preview** - a seek-aware, temporary loopback URL for a running torrent; no full-file memory buffering and no public media listener
- **Torrent encryption and SOCKS5** - prefer/require/disable peer encryption, optional proxy authentication with a DPAPI-encrypted password, remote proxy DNS and no direct fallback
- **IP blocklist** - bounded local IPv4/IPv6 CIDR lists with merged-range binary lookup, applied to incoming/outgoing peers and tracker/webseed destinations
- **Torrent creation** — native file/folder wizard, optional private trackers, v1 or hybrid v1/v2 metadata, background hashing and cancellation
- **Client migration** — import .torrent metadata, uTorrent/BitTorrent resume.dat paths/labels or qBittorrent BT_backup save paths/categories; imports remain paused pending recheck
- **RSS and watched-folder intake** — HTTP/HTTPS feeds, RSS/Atom enclosures, include/exclude title regex, category/save-folder routing and bounded persistent duplicate history; auto-download is off by default
- **Saved table layout** — column widths/order and sort choices persist across app restarts
- **Pause persistence** — manually paused torrents stay paused after restart
- **Atomic resume checkpoints** - serialized progress writes avoid overlapping startup/state saves; the normal poll loop checkpoints every 30 seconds, with final saves on graceful shutdown
- **Persistent crash-surviving log** — the runtime log is mirrored to `%AppData%\Controllarr\logs\controllarr.log`, fsync'd on warnings/errors and every few lines (~5 MB rotation keeping one `.1` backup) so it survives an app crash or reboot; a **Reveal Log File** action opens it in Explorer
- **GitHub-release update check** — a "Check for Updates" action plus a settings toggle query the GitHub Releases API and open the latest release page (replaces the macOS Sparkle updater)
- **Category-based save paths** and post-complete move rules for Plex library handoff
- **Clean torrent folders** - per-category checkbox plus an uncategorized default, name/hash folders for single-file and multi-file intake, stable magnet paths, non-overwriting moves and restart persistence; existing files are not silently reorganized
- **Archive extractor** (.rar / .zip / .7z) via SharpCompress
- **Dangerous-file filter** per category with blocked extension lists
- **Seeding policy** — per-category or global max ratio / max seed time with hit-and-run protection
- **Health monitoring** — stall detection with reason codes, auto-reannounce recovery
- **Bandwidth scheduler** — time-of-day download/upload rate limiting
- **DPAPI credential storage** for WebUI/API and SOCKS5 passwords without access/password prompts; *arr API keys remain in the app-state file (`%AppData%\Controllarr\state.json`)
- **Enforced VPN adapter binding** - source-bound IPv4 torrent sockets pinned to the selected Windows interface; unavailable tunnels block all starts, including API/force-start/queue actions. Torrent DNS uses the adapter's DNS servers without system fallback; LAN WebUI/API listeners remain separate
- **VPN-bound DHT** - bootstrap DNS and DHT datagrams use policy-owned sockets, including blocklist filtering, bounded queues and cache recovery. Automatic recognition includes PIA, NordVPN/NordLynx and popular generic tunnels; ambiguous matches require a selection
- **Explicit networking limits** - VPN/proxy modes block IPv6 fallback; LSD/router mapping stay disabled in protected modes. SOCKS5 disables DHT, UDP trackers and incoming peers. Network topology changes block torrents until restart. Keep the provider's kill switch enabled; the app cannot override its LAN firewall rules
- **Disk-space-aware auto-pause** — monitors free space, pauses downloads when below threshold, and exposes operator recheck in the UI
- ***arr re-search integration** — proactive Sonarr / Radarr callbacks when torrents stall beyond a configurable threshold
- **Session auth with expiry** — 1-hour token TTL, CORS support, cookie-based middleware
- **Backup export / restore** — download current state as JSON and restore it from the UI or API; current export buttons include decrypted secrets, so protect backups as sensitive plaintext
- **Recovery rules and recovery center** — automatically respond to unhealthy torrents with configurable delay-based rule escalation, and keep an action history of automatic/manual recovery attempts
- **Per-torrent save path** — `savepath` override from *arr apps wired through to MonoTorrent
- **Magnet URI protocol handler** — registers as the system handler for `magnet:` links
- **.torrent file support** — drag-and-drop or file picker from the native UI
- **System tray integration** — menu bar icon with port display, torrent count, and transfer rates

---

## UI Overview

### Sidebar Navigation

| Tab | Description |
|-----|-------------|
| **Home** | Optional dashboard with session metrics, status and most-active transfers |
| **Transfers** | Default native workspace with combined filters, multi-selection, bulk context-menu actions and Files/Trackers/Peers inspector |
| **Categories** | Category editor - save/complete paths, per-torrent subfolders, archive extraction, blocked extensions, ratio/time overrides |
| **Settings** | Full settings form — WebUI, port range, seeding policy, health, VPN, disk space, *arr, bandwidth, recovery rules, backup/restore |
| **Health** | Stall detection dashboard — reason classification, duration tracking, clear/recover actions |
| **Recovery** | Recovery action log — trigger, action, source (auto/manual), success/failure |
| **Post-Processor** | Post-completion pipeline status — move/extract stages, retry failed operations |
| **Seeding** | Seeding enforcement log — ratio/time limit actions with hit-and-run protection |
| ***arr** | Sonarr/Radarr re-search notification log |
| **Log** | Filterable log viewer with level coloring (Debug/Info/Warn/Error) and a Reveal Log File action that opens the on-disk log in Explorer |
| **RSS / Watch Folder** | Feed/rule editor, automatic intake, watch-folder configuration and recent entry status |

### Status Bar

The bottom status bar displays:
- Bulk-operation progress and cancellation control
- Current listen port
- Download / upload speeds (live)
- Connected peers / global connection limit
- VPN status pill (Connected / Disconnected / Kill Switch Engaged)
- Normal minimize keeps the taskbar button; closing to tray is configurable in Settings

---

## Web UI

The browser WebUI is a separate remote-management interface, not the desktop renderer. Open <http://127.0.0.1:8791> and log in with the default credentials `admin` / `adminadmin`, then change them before allowing LAN access. Its assets are embedded in the executable and extracted into the profile on boot, so no loose WebUI folder or separate build is required. The desktop window talks directly to the engine and does not use browser authentication.

---

## qBittorrent API Compatibility

Controllarr implements the qBittorrent Web API v2 surface used by Sonarr and Radarr. No special download client type or plugin is needed — select **qBittorrent** in your *arr app and point it at `http://<host>:8791`. Seerr/Overseerr sends requests through Sonarr/Radarr, not directly to a download client.

### Supported Endpoints

| Category | Endpoints |
|----------|-----------|
| **Auth** | `POST /api/v2/auth/login`, `POST /api/v2/auth/logout` |
| **App** | `GET /api/v2/app/version`, `webapiVersion`, `buildInfo`, `preferences`; `POST setPreferences` |
| **Transfer** | `GET /api/v2/transfer/info`, `speedLimitsMode` |
| **Torrents** | `GET info`, `properties`, `files`, `trackers`, `pieceStates`; `POST add`, `pause`, `resume`, `delete`, `setCategory` |
| **Categories** | `GET categories`; `POST createCategory`, `editCategory`, `removeCategories` |

### Controllarr-Native API

Extended endpoints at `/api/controllarr/*` for full access to all services:

| Endpoint | Description |
|----------|-------------|
| `GET /api/controllarr/stats` | Session stats, including `connected_peers` / `connection_limit` |
| `POST /api/controllarr/port/cycle` | Force listen port cycle |
| `GET/POST /api/controllarr/categories` | Extended category management (complete path, extract archives, blocked extensions, ratio/time overrides) |
| `GET/POST /api/controllarr/settings` | Full settings read/write |
| `GET /api/controllarr/backup`; `POST /api/controllarr/backup/import` | Export/import state backup (exports include secrets) |
| `GET /api/controllarr/health` | Health issue list |
| `GET /api/controllarr/recovery` | Recovery action log |
| `GET /api/controllarr/postprocessor` | Post-processor status |
| `GET /api/controllarr/seeding` | Seeding enforcement log |
| `GET /api/controllarr/diskspace` | Disk space status |
| `GET /api/controllarr/vpn` | VPN monitor status |
| `GET /api/controllarr/network` | Enforced socket policy, bound adapter/address and restart requirement |
| `GET/POST /api/controllarr/torrents/{hash}/options` | Connection/upload-slot overrides and sequential mode |
| `GET /api/controllarr/arr` | *arr notification log |
| `GET /api/controllarr/log` | Application log (query: `limit`) |
| `GET /api/controllarr/torrents/{hash}/files` | Per-torrent file list |
| `GET /api/controllarr/torrents/{hash}/trackers` | Per-torrent trackers |
| `GET /api/controllarr/torrents/{hash}/peers` | Per-torrent peers |

---

## Configuration

Settings are stored in `%AppData%\Controllarr\state.json` and editable from the native UI Settings tab or the API.

| Setting | Default | Description |
|---------|---------|-------------|
| `listen_port_range_start` | `49152` | Start of random port range |
| `listen_port_range_end` | `65000` | End of random port range |
| `stall_threshold_minutes` | `10` | Minutes of zero download before port cycle |
| `default_save_path` | `%UserProfile%\Downloads\Controllarr` | Default torrent save path |
| `web_ui_host` | `127.0.0.1` | HTTP server bind address |
| `web_ui_port` | `8791` | HTTP server port |
| `web_ui_username` | `admin` | WebUI / API username |
| `web_ui_password` | `adminadmin` | WebUI / API password (DPAPI-encrypted at rest) |
| `global_max_ratio` | *unlimited* | Global share ratio limit |
| `global_max_seeding_time_minutes` | *unlimited* | Global seeding time limit |
| `seed_limit_action` | `pause` | Action on ratio/time limit: `pause`, `remove_keep_files`, `remove_delete_files` |
| `minimum_seed_time_minutes` | `60` | Hit-and-run protection minimum |
| `health_stall_minutes` | `30` | Minutes before health flags a stall |
| `health_reannounce_on_stall` | `true` | Auto-reannounce when stall detected |
| `vpn_enabled` | `false` | Enforce selected-adapter torrent sockets and fail-closed starts |
| `vpn_kill_switch` / `vpn_bind_interface` | `true` | Legacy flags; cannot weaken enabled VPN enforcement |
| `vpn_interface_prefix` | `TAP` | VPN adapter name prefix (TAP, Wintun, etc.) |
| `vpn_interface_id` | *automatic* | Explicit Windows tunnel ID overrides name matching; restart required |
| `torrent_network` | *direct / Prefer encryption* | Encryption, SOCKS5 and local blocklist settings; see the networking guide |
| `vpn_monitor_interval_seconds` | `5` | VPN check interval |
| `disk_space_minimum_gb` | *disabled* | Minimum free GB before auto-pause |
| `arr_re_search_after_hours` | `6` | Hours before triggering *arr re-search |
| `torrent_queueing` | *disabled* | Active download/seed/total caps; 0 allows none in that class |
| `connection_limits.global_max_connections` | `200` | Bounded shared peer limit; native/browser saves apply live |
| `connection_limits.download_reserve_percent` | `25` | Download headroom while downloads are active; 0 disables seed budgeting |
| `global_download_kbps` / `global_upload_kbps` | `0` | Global KiB/s limits; 0 is unlimited |
| `rss_feeds` | `[]` | Feed rules; auto-download is opt-in |
| `watch_folder` | *disabled* | Absolute folder containing stable .torrent files to import |

---

## Architecture

The native window composes its runtime services directly in `MainViewModel`
and delegates transfer-list behavior to `DesktopViewModel`. The core also
contains a separate `ControllarrRuntime` orchestrator for alternate hosts.

```
Controllarr.App (WPF)
  └── MainViewModel (@Observable, 2s polling)
       └── Runtime services + DesktopViewModel
            ├── TorrentEngine (MonoTorrent wrapper)
            │    └── MonoTorrent.ClientEngine
            ├── PersistenceStore → %AppData%\Controllarr\state.json
            ├── CredentialStore → DPAPI-encrypted credentials
            ├── PortWatcher ─── polls every 30s, cycles on stall
            ├── HealthMonitor ─── fed by 2s tick loop
            ├── PostProcessor ─── fed by 2s tick loop
            ├── SeedingPolicy ─── fed by 2s tick loop
            ├── RecoveryCenter ─── fed by 2s tick loop
            ├── ArrNotifier ─── fed by 2s tick loop
            ├── BandwidthScheduler ─── self-polls every 60s
            ├── RssService ─── bounded feed checks and watched-folder imports
            ├── DiskSpaceMonitor ─── self-polls every 30s
            ├── VPNMonitor ─── self-polls every 5s
            └── HttpServer (ASP.NET Core Kestrel)
                 ├── qBittorrent API v2 routes
                 ├── Controllarr-native API routes
                 └── Static WebUI server (SPA fallback)
```

### Tick Loop (2s cadence)

```
engine.TickQueueAsync(transferGuardAllows)
engine.ApplyPendingFileFilters()
engine.PollStats() → TorrentStats[]
  → PostProcessor.Tick(torrents)
  → SeedingPolicy.Tick(torrents)
  → HealthMonitor.Tick(torrents)
  → RecoveryCenter.Tick()
  → ArrNotifier.Tick()
```

### Project Structure

```
Controllarr-Windows/
├── Controllarr.sln
├── src/
│   ├── Controllarr.Core/               ← Class library
│   │   ├── Engine/
│   │   │   ├── TorrentEngine.cs         # MonoTorrent wrapper, stats, file/tracker/peer info
│   │   │   ├── ITorrentEngine.cs        # Service abstraction interface
│   │   │   └── TorrentView.cs           # Read-only torrent snapshot
│   │   ├── Services/
│   │   │   ├── PortWatcher.cs           # Automatic port cycling on stall
│   │   │   ├── HealthMonitor.cs         # Stall detection + reason classification
│   │   │   ├── PostProcessor.cs         # Move + extract pipeline
│   │   │   ├── SeedingPolicy.cs         # Ratio/time enforcement
│   │   │   ├── BandwidthScheduler.cs    # Time-of-day rate limiting
│   │   │   ├── DiskSpaceMonitor.cs      # Free space monitoring
│   │   │   ├── VPNMonitor.cs            # TAP/WireGuard/Wintun detection + kill switch
│   │   │   ├── ArrNotifier.cs           # Sonarr/Radarr re-search
│   │   │   ├── RecoveryCenter.cs        # Rule engine + action log
│   │   │   └── Logger.cs               # Ring-buffer logger
│   │   ├── Persistence/
│   │   │   ├── Models.cs                # Settings, Category, BandwidthRule, ArrEndpoint, etc.
│   │   │   ├── PersistenceStore.cs      # JSON state store (debounced writes)
│   │   │   └── CredentialStore.cs       # DPAPI credential encryption
│   │   ├── Server/
│   │   │   ├── HttpServer.cs            # ASP.NET Core Kestrel embedded server
│   │   │   ├── QBittorrentApi.cs        # qBit v2 + Controllarr-native endpoints
│   │   │   └── FormParser.cs            # URL-encoded + multipart form parsing
│   │   └── Runtime.cs                   # Umbrella orchestrator
│   └── Controllarr.App/                 ← WPF application
│       ├── App.xaml / App.xaml.cs        # Entry point, single-instance, magnet: handler
│       ├── MainWindow.xaml              # Sidebar + status bar + content area
│       ├── Themes/Dark.xaml             # Windows 11-style dark theme (1000+ lines)
│       ├── Helpers/DirtyTracker.cs      # Attached behavior for edit detection
│       ├── ViewModels/
│       │   └── MainViewModel.cs         # Central MVVM ViewModel (2s polling)
│       └── Views/
│           ├── TorrentWorkspace.xaml    # Virtualized native transfers + bulk selection + inspector
│           ├── CategoriesView.xaml      # Category editor
│           ├── SettingsView.xaml        # Full settings form
│           ├── HealthView.xaml          # Health issue dashboard
│           ├── LogView.xaml             # Filterable log viewer
│           └── SimpleViews.xaml         # Recovery, PostProcessor, Seeding, *arr, Disk, VPN
└── docs/
    └── assets/
        └── icon-256.png
```

---

## Differences from the macOS Version

| Aspect | macOS | Windows |
|--------|-------|---------|
| **Torrent engine** | libtorrent-rasterbar (C++ via Obj-C++ shim) | MonoTorrent (pure .NET) |
| **UI framework** | SwiftUI + AppKit | WPF with custom dark theme |
| **HTTP server** | Hummingbird (Swift) | ASP.NET Core Kestrel |
| **Credential storage** | macOS Keychain | Windows DPAPI |
| **Archive extraction** | macOS bsdtar | SharpCompress (.NET) |
| **VPN detection** | `getifaddrs()` for utun interfaces | `NetworkInterface` for TAP/WireGuard/Wintun |
| **Auto-update** | Sparkle | GitHub Releases update check |
| **Web UI** | Bundled React SPA | Bundled no-build static SPA (served at :8791) |

---

## Build from Source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later. We stay on the `net8.0-windows` LTS target framework. The repo ships a `NuGet.config` pointing at [nuget.org](https://www.nuget.org/), so a fresh clone restores all dependencies (MonoTorrent, SharpCompress, ASP.NET Core) without any extra feed configuration.

Run `dotnet run --project tests/Controllarr.Desktop.Tests -c Release` for headless catalog/batch tests and `dotnet run --project tests/Controllarr.Windows.UI.Tests -c Release` on Windows for native XAML/selection tests. The Windows CI workflow builds and uploads complete-folder architecture packages but does not publish a GitHub release. Cross-building from macOS/Linux requires `-p:EnableWindowsTargeting=true`; it is not a substitute for Windows runtime testing. With Node.js, `node scripts/test-site.cjs` checks the site's architecture-specific downloads and fallback behavior.

```bash
git clone https://github.com/eMacTh3Creator/Controllarr-Windows.git
cd Controllarr-Windows

# Debug build
dotnet build

# Self-contained Windows packages: publish both architectures
dotnet publish src/Controllarr.App/Controllarr.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=false -o publish/win-x64
dotnet publish src/Controllarr.App/Controllarr.App.csproj -c Release -r win-arm64 --self-contained true \
  -p:PublishSingleFile=false -o publish/win-arm64
```

Distribute the complete folder, never just its EXE. WebUI assets are embedded; no separate .NET install is needed. On Windows, `scripts/package-windows.ps1` with Inno Setup 6 builds both CPU installers, portable ZIPs and checksums. CI produces the same formats without publishing a release. See [INSTALL.md](docs/INSTALL.md).

### Open in Visual Studio

Open `Controllarr.sln` in Visual Studio 2022. Set `Controllarr.App` as the startup project and press F5.

---

## VPN Setup (Windows)

Controllarr recognizes active IPv4 tunnel names/descriptions including:

- `TAP-Windows` / `TAP-Win32` (OpenVPN)
- `WireGuard`
- `Wintun` (WireGuard kernel driver)
- PIA / Private Internet Access, NordVPN / NordLynx, Proton VPN, Mullvad, Surfshark, ExpressVPN, CyberGhost, Windscribe and IVPN
- Or any adapter whose name starts with the configured `vpn_interface_prefix`

When VPN is enabled in settings:

1. **VPN connected** - torrent sockets use the selected adapter's IPv4 source address and Windows interface index.
2. **VPN unavailable** - policy-owned sockets close and all torrent starts are blocked, including API and force-start actions. Legacy bind/kill-switch flags cannot weaken enabled enforcement.
3. **VPN reconnects** - eligible queued torrents recover; manually paused torrents stay paused.

Select your actual VPN tunnel, not Ethernet/Wi-Fi. Automatic detection fails closed if multiple tunnels match; explicit selection is preferred on a multi-VPN machine. Enable DHT in Settings for public trackerless magnets. DHT DNS/UDP are forced through that adapter, while protected IPv6, LSD and router mapping remain disabled. SOCKS5 cannot use DHT. VPN/proxy/blocklist topology changes require restart. LAN WebUI/API stays separate, but provider and Windows firewall rules still apply. Keep the provider's kill switch enabled. This is app-level socket enforcement, not an OS-wide leak guarantee; read [NETWORKING.md](docs/NETWORKING.md).

---

## Migrating from qBittorrent

1. In Sonarr / Radarr, change the download client URL from `http://localhost:8080` to `http://localhost:8791`
2. Update the username/password to `admin` / `adminadmin` (or whatever you set in Controllarr)
3. Keep the client type as **qBittorrent** — no plugin or custom type needed
4. Controllarr handles category creation, save paths, and post-import cleanup the same way

---

## Documentation

| Document | Description |
|----------|-------------|
| [docs/README.md](docs/README.md) | Documentation index and overview |
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | Day-to-day operations, deployment, and troubleshooting guide |
| [docs/STORAGE.md](docs/STORAGE.md) | Category folders, trailing separators, safe moves, existing downloads and archive scope |
| [docs/PERFORMANCE.md](docs/PERFORMANCE.md) | Performance tuning and large-library guidance |
| [docs/STALL_AUDIT.md](docs/STALL_AUDIT.md) | Metadata/tracker stall findings, source repairs and remaining work |
| [docs/V1_5_ROADMAP.md](docs/V1_5_ROADMAP.md) | Roadmap and feature planning |
| [docs/index.html](docs/index.html) | Project landing page |
| [Releases](https://github.com/eMacTh3Creator/Controllarr-Windows/releases) | Pre-built binaries and release notes |

---

## License

[MIT](LICENSE) — Controllarr is original work. It reimplements qBittorrent-compatible behavior from public specs; no GPL-licensed qBittorrent source is included or referenced during development. On Windows, the WebUI/API password is encrypted at rest with the Windows Data Protection API (DPAPI); *arr API keys are stored in the app-state file.
