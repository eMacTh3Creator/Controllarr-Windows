# Performance and Large Libraries

The native desktop targets responsive management of thousands of torrents.
Library size is not the same as simultaneously active downloads: disk speed,
peer churn, hashing and tracker behavior still determine achievable throughput.
Do not treat a synthetic row test as a guarantee of production stability.

## v2.2.4 Storage Safety

Subfolder routing is resolved once at intake and captured in per-torrent state.
Ordinary two-second stats reads do not recursively enumerate storage directories.
Completed extraction uses that torrent's selected file inventory rather than
scanning a potentially huge shared category tree. Moves remain serial and stop
the torrent first; cross-disk moves still depend on disk throughput. Synchronous
post-processing remains an outstanding service-I/O optimization, not solved by
this layout update. See [STORAGE.md](STORAGE.md).

## v2.2.3 Discovery Repairs

v2.2.3 adds four-worker, bounded/deduplicated peer rediscovery with
backoff, correct tracker failover/intervals, UDP-first bound DNS and selected-file
queue completion. Torrent intake uses asynchronous engine calls and lightweight
registration saves, not whole-library resume checkpoints on every add; API
intake batches save every 25 successes and at the end. Socket pruning runs at
most once per second during activity rather than scanning all connections on
each connect. See [STALL_AUDIT.md](STALL_AUDIT.md) for remaining service-I/O,
download fairness and metadata queue-policy work. DHT uses one reusable UDP
socket, bounded message/lookup queues, a bounded/expiring peer store and one
maintenance timer, not a thread or socket per torrent. Bootstrap DNS is
cancellable and follows the enforced adapter. DHT is still disabled for SOCKS5.

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
- Most bulk actions execute serially on a worker, with partial failures and
  cancellation. Removal pauses the whole selection first, overlaps up to 16
  stop operations (two-second final tracker waits), then deletes files serially.
  Cancel still stops selected startup/metadata transfers, but prevents further
  deletions. In-flight disk work finishes safely.
- Bulk imports checkpoint every 25 successful changes. Removal uses lightweight
  engine/paused/options saves every 100 processed targets or five seconds and
  at the end, without rewriting the remaining library's fast-resume files.
  API deletion also saves category state only once per batch.
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
- Queue positions compact once after a removal batch, not after every row;
  legacy positions normalize on startup and unknown hash probes cannot consume ranks.
- Download-first slot admission and configurable peer headroom prevent ordinary
  seeds from using the full global connection budget while downloads are eligible.
  Seed caps are effective runtime limits, not destructive edits to saved overrides.
  At most eight scheduler stop/reconnect workers run; disk/network completion
  still finishes safely, with no new starts if a stop fails.
- RSS intake uses one cancellable worker with bounded bodies/history and one
  engine/history checkpoint per scan rather than per feed entry.

v2.2.1 controlled VM removal benchmark: 2,376 paused local 16 KiB fixture torrents
and their files removed in 37.37 seconds. Eight active fixtures with a tracker
that accepted HTTP connections but never replied were removed in 2.06 seconds.
These are disposable local checks, not real-peer, SMB or large-payload guarantees.

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
The status bar exposes Peers used/limit. If full, use active seed/per-torrent
caps and download connection reserve before raising the global limit. Reserve
defaults to 25% (0-90%, 0 off) and also operates when active-torrent queueing is
disabled. Ordinary seeds share the remaining budget; extra seeds queue, then
return when no downloads are eligible. Forced seeds retain their controls and
can consume headroom. This does not guarantee seed availability, uniform
connections per download, or immediate connection recycling. Review private
tracker requirements before queuing seeds. Browser saves apply these limits live.
These controls and the default save folder apply when settings are saved.
Preferred listen-port changes can apply live; WebUI listener and torrent
VPN/proxy/blocklist topology changes require a restart. DHT works through VPN
binding and blocklists; SOCKS5 still disables it. Local discovery/router mapping
remain disabled in protected modes.

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
