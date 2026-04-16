<p align="center">
  <img src="docs/assets/icon-256.png" alt="Controllarr icon" width="160" height="160" />
</p>

<h1 align="center">Controllarr for Windows</h1>

<p align="center">A native Windows BitTorrent client built for Sonarr / Radarr / Overseerr / Plex workflows.</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%2F11-blue" alt="Windows 10/11" />
  <img src="https://img.shields.io/badge/.NET_8-net8.0--windows-purple" alt=".NET 8" />
  <img src="https://img.shields.io/badge/x64-supported-green" alt="x64" />
  <img src="https://img.shields.io/badge/license-MIT-green" alt="MIT License" />
</p>

---

Controllarr for Windows is a full native port of the [macOS Controllarr](https://github.com/eMacTh3Creator/Controllarr) BitTorrent client. It uses [MonoTorrent](https://github.com/alanmcgovern/monotorrent) as its torrent engine and wraps it in a WPF desktop app with a modern Windows 11-style dark theme. It speaks the qBittorrent Web API so existing *arr apps can point at it with zero extra configuration.

**Status:** v1.2.0 — production-ready `.exe` with native WPF UI, qBittorrent Web API compatibility, post-processing pipeline, seeding policy, health monitoring, bandwidth scheduler, per-torrent file/tracker/peer detail, DPAPI credential storage, disk-space-aware auto-pause, *arr re-search integration, VPN-aware kill switch with interface binding, recovery center with rule chaining, and backup/restore. See [Releases](https://github.com/eMacTh3Creator/Controllarr-Windows/releases) for a pre-built binary.

## Download

| Platform | Download | Requirements |
|----------|----------|--------------|
| **Windows x64** | [Controllarr.exe](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/latest) | Windows 10/11 (self-contained, no .NET install needed) |

Download `Controllarr.exe` from the [latest release](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/latest) and run it. No installer required — single portable executable.

On first launch, the Web UI is available at <http://127.0.0.1:8791> — default login is `admin` / `adminadmin`. Point Sonarr / Radarr at the same URL using the qBittorrent download client type.

---

## Features

- **Automatic listen-port reselection** when the forwarded port goes offline (the #1 reason this project exists)
- **qBittorrent Web API v2** compatibility — Sonarr / Radarr / Overseerr work without custom integration
- **Native Windows UI** with sidebar navigation: Torrents, Categories, Settings, Health, Recovery, Post-Processor, Seeding, *arr, Log
- **Modern dark theme** — Windows 11-style Fluent Design with glass-morphism effects
- **Per-torrent detail** — file picker (skip/enable individual files), tracker status, live peer list
- **Category-based save paths** and post-complete move rules for Plex library handoff
- **Archive extractor** (.rar / .zip / .7z) via SharpCompress
- **Dangerous-file filter** per category with blocked extension lists
- **Seeding policy** — per-category or global max ratio / max seed time with hit-and-run protection
- **Health monitoring** — stall detection with reason codes, auto-reannounce recovery
- **Bandwidth scheduler** — time-of-day download/upload rate limiting
- **DPAPI credential storage** for the WebUI password and *arr API keys (Windows Data Protection API)
- **VPN kill switch** — detects TAP-Windows, WireGuard, and Wintun VPN adapters and pauses all torrents instantly when the VPN drops; auto-resumes on reconnect
- **VPN interface binding** — binds MonoTorrent's outgoing and listen interfaces to the VPN adapter so torrent traffic never leaks through the default route
- **Disk-space-aware auto-pause** — monitors free space, pauses downloads when below threshold, and exposes operator recheck in the UI
- ***arr re-search integration** — proactive Sonarr / Radarr callbacks when torrents stall beyond a configurable threshold
- **Session auth with expiry** — 1-hour token TTL, CORS support, cookie-based middleware
- **Backup export / restore** — download the current state as JSON, optionally include encrypted secrets, and restore it from the UI or API
- **Recovery rules and recovery center** — automatically respond to unhealthy torrents with configurable delay-based rule escalation, and keep an action history of automatic/manual recovery attempts
- **Per-torrent save path** — `savepath` override from *arr apps wired through to MonoTorrent
- **Magnet URI protocol handler** — registers as the system handler for `magnet:` links
- **.torrent file support** — drag-and-drop or file picker from the native UI
- **System tray integration** — menu bar icon with port display, torrent count, and transfer rates

---

## UI Overview

### Sidebar Navigation (9 tabs)

| Tab | Description |
|-----|-------------|
| **Torrents** | Main torrent list with progress bars, speeds, context menu actions, add magnet/file |
| **Categories** | Category editor — save path, complete path, archive extraction, blocked extensions, ratio/time overrides |
| **Settings** | Full settings form — WebUI, port range, seeding policy, health, VPN, disk space, *arr, bandwidth, recovery rules, backup/restore |
| **Health** | Stall detection dashboard — reason classification, duration tracking, clear/recover actions |
| **Recovery** | Recovery action log — trigger, action, source (auto/manual), success/failure |
| **Post-Processor** | Post-completion pipeline status — move/extract stages, retry failed operations |
| **Seeding** | Seeding enforcement log — ratio/time limit actions with hit-and-run protection |
| ***arr** | Sonarr/Radarr re-search notification log |
| **Log** | Filterable log viewer with level coloring (Debug/Info/Warn/Error) |

### Status Bar

The top status bar displays:
- Connection status indicator (green/red dot)
- Current listen port
- Download / upload speeds (live)
- VPN status pill (Connected / Disconnected / Kill Switch Engaged)
- Disk pressure indicator

---

## qBittorrent API Compatibility

Controllarr implements the qBittorrent Web API v2 surface that Sonarr, Radarr, and Overseerr use. No special download client type or plugin is needed — just select **qBittorrent** in your *arr app and point it at `http://<host>:8791`.

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
| `GET /api/controllarr/stats` | Session stats (native format) |
| `POST /api/controllarr/port/cycle` | Force listen port cycle |
| `GET/POST /api/controllarr/categories` | Extended category management (complete path, extract archives, blocked extensions, ratio/time overrides) |
| `GET/POST /api/controllarr/settings` | Full settings read/write |
| `GET/POST /api/controllarr/backup` | Export/import state backup |
| `GET /api/controllarr/health` | Health issue list |
| `GET /api/controllarr/recovery` | Recovery action log |
| `GET /api/controllarr/postprocessor` | Post-processor status |
| `GET /api/controllarr/seeding` | Seeding enforcement log |
| `GET /api/controllarr/diskspace` | Disk space status |
| `GET /api/controllarr/vpn` | VPN monitor status |
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
| `default_save_path` | `~/Downloads/Controllarr` | Default torrent save path |
| `webui_host` | `127.0.0.1` | HTTP server bind address |
| `webui_port` | `8791` | HTTP server port |
| `webui_username` | `admin` | WebUI / API username |
| `webui_password` | `adminadmin` | WebUI / API password (DPAPI-encrypted) |
| `global_max_ratio` | *unlimited* | Global share ratio limit |
| `global_max_seeding_time_minutes` | *unlimited* | Global seeding time limit |
| `seed_limit_action` | `pause` | Action on ratio/time limit: `pause`, `remove_keep_files`, `remove_delete_files` |
| `minimum_seed_time_minutes` | `60` | Hit-and-run protection minimum |
| `health_stall_minutes` | `30` | Minutes before health flags a stall |
| `health_reannounce_on_stall` | `true` | Auto-reannounce when stall detected |
| `vpn_enabled` | `false` | Enable VPN monitoring |
| `vpn_kill_switch` | `true` | Pause torrents when VPN drops |
| `vpn_bind_interface` | `true` | Bind traffic to VPN adapter |
| `vpn_interface_prefix` | `TAP` | VPN adapter name prefix (TAP, Wintun, etc.) |
| `vpn_monitor_interval_seconds` | `5` | VPN check interval |
| `disk_space_minimum_gb` | *disabled* | Minimum free GB before auto-pause |
| `arr_re_search_after_hours` | `6` | Hours before triggering *arr re-search |

---

## Architecture

```
Controllarr.App (WPF)
  └── MainViewModel (@Observable, 2s polling)
       └── ControllarrRuntime (orchestrator)
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
            ├── DiskSpaceMonitor ─── self-polls every 30s
            ├── VPNMonitor ─── self-polls every 5s
            └── HttpServer (ASP.NET Core Kestrel)
                 ├── qBittorrent API v2 routes
                 ├── Controllarr-native API routes
                 └── Static WebUI server (SPA fallback)
```

### Tick Loop (2s cadence)

```
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
│           ├── TorrentsView.xaml        # Torrent list + toolbar + context menu
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
| **Auto-update** | Sparkle | — (planned) |
| **Web UI** | Bundled React SPA | — (API-compatible, use browser) |

---

## Build from Source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.

```bash
git clone https://github.com/eMacTh3Creator/Controllarr-Windows.git
cd Controllarr-Windows

# Debug build
dotnet build

# Release build (self-contained single-file .exe)
dotnet publish src/Controllarr.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish
```

The resulting `publish/Controllarr.exe` is a fully self-contained executable — no .NET runtime installation required.

### Open in Visual Studio

Open `Controllarr.sln` in Visual Studio 2022. Set `Controllarr.App` as the startup project and press F5.

---

## VPN Setup (Windows)

Controllarr detects VPN adapters by scanning network interfaces for adapters with descriptions matching:

- `TAP-Windows` / `TAP-Win32` (OpenVPN)
- `WireGuard`
- `Wintun` (WireGuard kernel driver)
- Or any adapter whose name starts with the configured `vpn_interface_prefix`

When VPN is enabled in settings:

1. **VPN connected** — Controllarr binds all torrent traffic to the VPN adapter IP
2. **VPN disconnected + kill switch on** — all active torrents are instantly paused
3. **VPN reconnects** — paused torrents automatically resume

This ensures torrent traffic **never leaks** through your default network interface.

---

## Migrating from qBittorrent

1. In Sonarr / Radarr, change the download client URL from `http://localhost:8080` to `http://localhost:8791`
2. Update the username/password to `admin` / `adminadmin` (or whatever you set in Controllarr)
3. Keep the client type as **qBittorrent** — no plugin or custom type needed
4. Controllarr handles category creation, save paths, and post-import cleanup the same way

---

## License

[MIT](LICENSE) — Controllarr is original work. It reimplements qBittorrent-compatible behavior from public specs; no GPL-licensed qBittorrent source is included or referenced during development.
