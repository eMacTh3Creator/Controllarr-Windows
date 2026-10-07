# Performance and Large Libraries

The native desktop targets responsive management of thousands of torrents.
Library size is not the same as simultaneously active downloads: disk speed,
peer churn, hashing and tracker behavior still determine achievable throughput.
Do not treat a synthetic row test as a guarantee of production stability.

## Implemented in the Desktop Redesign

- WPF DataGrid row/column virtualization, recycling and a bounded layout; no
  browser renderer or Edge subprocesses in the desktop window.
- Stable, hash-indexed row models. Steady-state polling changes only properties
  whose values changed, not the entire collection. Multi-selection is restored
  across necessary view refreshes, but hidden selections are dropped.
- Search debounce (180 ms) and combined category/status/search filters.
- Engine manager lookup by hash rather than scanning the whole library for
  every selected torrent action.
- A 250 ms engine snapshot cache shared by closely spaced desktop/API/service
  reads, with mutation invalidation. Session counters reuse that snapshot.
- Bulk operations execute serially on a worker, with partial failure reporting
  and cancellation before the next torrent. In-flight operations finish first.
- Bulk imports/removals checkpoint engine state every 25 successful changes and again
  at the end, instead of serializing the entire library for every removed row.
- Engine state writes are serialized and use a temporary file plus replacement.
- Resume progress uses compatible atomic checkpoints under one writer, rather
  than concurrent library startup/state writes. The poll loop checkpoints every
  30 seconds; explicit state saves and graceful shutdown also checkpoint.
- Engine settings mutations are serialized so speed/port tuning cannot
  overwrite the peer-encryption policy from an older settings snapshot.
- Files/trackers/peers are loaded on demand off the UI thread. Detail loads are
  serialized and stale selections cannot overwrite the latest inspector.
- Settings/category editors are not replaced while open. Log, post-processing
  and seeding table publication is limited to their active native pages.
- Startup engine restoration is offloaded from the WPF UI thread.
- Active queue planning caps ordinary downloads/seeds/total separately from
  library size. Stop operations precede newly admitted starts. Force-start is
  explicit; manually paused torrents and errors are excluded.
- RSS intake uses one cancellable worker with bounded bodies/history and one
  engine/history checkpoint per scan rather than per feed entry.

## Validation

```powershell
dotnet run --project tests/Controllarr.Desktop.Tests -c Release
dotnet run --project tests/Controllarr.Windows.UI.Tests -c Release
```

The first runner checks identity, filters, notifications, batch deduplication,
partial failures and cancellation, and measures 50 catalog reconciliations
at 1,000 and 10,000 rows. It excludes MonoTorrent snapshots, WPF rendering,
disk/network activity and snapshot-generation allocations.
On Windows it also exercises real local file verification, both removal modes
on disposable payloads, engine-state restoration and paused-state persistence.
Add `-- --engine-load` for the opt-in 1,000-torrent local-engine fixture run.

The second runner needs Windows and checks XAML loading and real WPF
multi-selection/filter refreshes and actual viewport virtualization without starting the engine or accessing
the user's profile. The CI workflow runs both on a Windows runner.

The October 7, 2026 VM run rendered 13 rows for 10,000 WPF items. A separate
preview.2 1,000-torrent engine run imported fixtures in 3.59 seconds and averaged
4.90 ms per uncached stats poll, with 42.3 MB runner private bytes at measurement.
Those torrents had tiny local files and no peers;
these are not production throughput or whole-app memory guarantees.
See [the validation report](NATIVE_DESKTOP_VALIDATION.md) for scope and caveats.

For live tests, use a separate profile as described in [DESKTOP.md](DESKTOP.md).
Record library size, active downloads/seeds, file counts, hardware, Release
build, memory/private bytes, CPU, disk latency and peer count. Measure with
the window open, minimized, in the tray and with a remote browser connected.
Repeat after pause/resume, category filtering, storage moves and app restart.

## Practical Tuning

The native Settings page exposes the global peer connection limit (200 by
default), per-torrent connection/upload-slot defaults, DHT, LSD, active queue
caps and global KiB/s limits. More connections are not automatically faster.
These controls and the default save folder apply when settings are saved.
Preferred listen-port changes can apply live; WebUI listener and torrent
VPN/proxy/blocklist topology changes require a restart. Protected discovery
remains disabled regardless of the saved DHT/LSD switches.

Use Task Manager/Resource Monitor and the persistent profile log to identify
the actual bottleneck. Avoid simultaneously checking a large library and
moving/extracting large archives on slow or network-mounted storage.

## Current Boundaries

The 2-second runtime still visits the torrent library and each torrent's file
statistics. Service ticks are coordinated, not falsely advertised as a fully
parallel pipeline. Active queueing and per-torrent connection budgets/upload
slots are enforced. The legacy global upload-slot field is a per-torrent
default, not an aggregate slot cap. IP blocklists are parsed once at startup,
merged and binary-searched; socket tracking scales with live connections rather
than the torrent catalog. Previews stream data in bounded buffers, with one
per torrent and four sessions total. There is no documented Windows
650-torrent automatic conservative mode; earlier copied macOS claims have
been removed from this guide.

Real-world multi-day load and VPN drop/reconnect tests remain necessary before
calling this a proven replacement for another client in a production library.
