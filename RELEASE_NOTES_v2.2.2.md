# v2.2.2 - Aware queues and download connection headroom

## Queue Repair

- Compact queue positions after single/bulk removals, including partial and cancelled batches. Startup repairs old gaps while preserving relative order and torrent controls.
- Newly added torrents append to the live library; emptying the library resets the next rank to 1. Unknown hash probes no longer consume positions.
- Eligible downloads precede ordinary seeds under a shared active-total cap; forced torrents retain precedence. Rank is library order, not an active-waiting count.

## Connection Budget and Diagnosis

- Download connection reserve: 25% default, configurable 0-90% in native/browser Settings; 0 disables it. While downloads/metadata transfers are eligible, ordinary seeds share the remaining global peer budget, and excess seeds queue even if active-torrent queueing is off.
- Effective seed caps preserve saved per-torrent overrides. Over-budget seeds reconnect when their cap changes; at most eight scheduler stop workers run before admitting new starts. Stop failures postpone new admission and retry safely.
- Global connection limit remains bounded and is never raised automatically. Forced seeds retain their caps and can consume headroom; this is not a hard per-download guarantee. Review private-tracker seeding requirements before queuing seeds.
- Global connected peers / limit in the desktop status bar and browser overview. Live selected-torrent reasons distinguish queue/zero limits, VPN/disk guards, metadata waits, full global/per-torrent caps and idle peers.
- Unknown tracker scrape counts display Unknown, not zero seeders. No connected peers alone does not prove an empty swarm.
- Browser global connection/queue changes apply live instead of only persisting. API adds `status_reason`, `connected_peers` and `connection_limit` without changing qBittorrent state strings.
- Pausing a metadata/hash-fetching torrent now stops transfer activity instead of relying on a library pause operation that ignores those modes.
- The scheduler releases sockets retained by ordinary paused torrents without resuming them, so idle paused connections cannot keep consuming the global peer budget.

## Upgrade and Limits

Finish/cancel any current batch, then Exit from the old app's tray. Run the
matching x64 or ARM64 Setup.exe and choose Keep existing settings. Settings,
categories, downloaded files and resume metadata are preserved; optional import
and portable ZIPs remain available. Queue ranks normalize automatically.

Apps/installers are unsigned; verify SHA256SUMS.txt. ARM64 remains experimental.
Windows VM x64 runs use ARM emulation. Synthetic/local fixtures and short API
prechecks are not multi-day stability, physical Intel/AMD, real-provider VPN
packet-capture or real-swarm throughput guarantees. No production profile was
used for tests. See [the validation report](docs/NATIVE_DESKTOP_VALIDATION.md).
