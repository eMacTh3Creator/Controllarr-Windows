# Torrent Stall Audit

October 7, 2026. These repairs ship in **v2.2.3**. Historical v2.2.2 installers
do not contain them. Windows uses MonoTorrent 3.0.2 with an adapted, licensed
DHT source component; the macOS engine is separate.
Scope: Windows/MonoTorrent, including the native desktop and remote API.
The photo shows zero throughput and zero connected peers, but cannot establish
swarm availability, provider routing or the cause of any machine restart.

## Findings and Repairs

### High: tracker failures could suppress failover

MonoTorrent 3.0.2's transport returns an Offline/InvalidResponse value for many
failures. Its tracker tier expects an exception to try the next tracker, and
otherwise marks the announce successful. HTTP `failure reason` responses can
even arrive with an OK transport state; these are rejected explicitly too.
The tracker implementation also does
not update its saved successful announce interval response. This can strand a
torrent behind a failed first tracker or use the wrong refresh cadence.

`Networking/ReliableTracker.cs` preserves the inner tracker's error/status for
diagnosis, converts unsuccessful responses to exceptions for tier failover,
and retains the server's successful minimum/regular intervals. Explicit refresh
can use the minimum interval, not the full regular interval. Peer ingestion
still goes through the library's normal tracker path, including private-torrent
handling. It does not fake Started events or repeatedly restart transfers.

Pinned upstream evidence:
[TrackerTier.cs](https://github.com/alanmcgovern/monotorrent/blob/e78faebd0aec117146cffccaaea987ab0629eec0/src/MonoTorrent.Client/MonoTorrent.Client.Tracker/TrackerTier.cs),
[Tracker.cs](https://github.com/alanmcgovern/monotorrent/blob/e78faebd0aec117146cffccaaea987ab0629eec0/src/MonoTorrent.Trackers/MonoTorrent.Trackers/Tracker.cs).

### High: VPN DNS supported TCP only

Tunnel DNS previously required TCP port 53. UDP-only adapter resolvers could
leave hostname trackers and webseeds unreachable even when numeric peer IPs
worked. `TunnelDns` now tries UDP first, with TCP fallback for truncation or
UDP transport failure. Both remain policy-owned, source/interface-bound in VPN
mode, bounded to five seconds per resolver, with no system-DNS fallback.

### High: metadata stalls escaped health checks and desktop recovery

Health only evaluated Downloading, excluding DownloadingMetadata. The desktop
also never attached the callback used by automatic health reannounce.
Both paths are now wired. Missing-peer/metadata discovery refresh starts after
one inactive minute, then backs off by 2, 4, 8 and at most 15 minutes, subject
to tracker minimums. The configured health timeout still controls issue
classification and escalation. Disabling auto-reannounce prevents these refreshes.

Refreshes use four workers, a 1,024-entry bounded queue and per-hash automatic
deduplication. Tracker I/O cannot block health snapshots or the desktop poll;
refresh attempts are capped at 35 seconds. Shutdown cancels queued/network work.
Paused, queued, removing, safety-blocked and unavailable-VPN torrents are not
refreshed or resumed. Metadata acquisition, byte-level progress and a positive
download rate reset the stall clock, avoiding float-rounding false positives.

### Medium: selected-file completion consumed download slots

The queue and UI used whole-torrent progress, including skipped files. A
completed selection could keep occupying the only download slot forever.
Scheduling, connection budgeting and displayed progress now use MonoTorrent's
selected-file `PartialProgress`. All-skipped files remain zero progress, not a
false completed download. Queue policies and manual pause remain authoritative.

### Medium: duplicate intake discarded useful trackers

Re-adding a hash returned immediately, ignoring the MergeTrackers setting.
New source-supplied trackers are now merged and persisted for non-private
torrents when that policy is selected, without resetting category/path/limits
or resuming paused work. Known private torrents are not modified automatically.
No public tracker list is injected. Ignore/Ask do not auto-merge; a dedicated
Ask confirmation workflow remains outside this repair. Tracker URL paths and
passkeys are deduplicated case-sensitively.

### Medium: intake and socket bookkeeping did unnecessary work

Intake used synchronous waits under a lock and checkpointed the whole library's
resume files for every new item. It now awaits engine operations under the
shared queue/removal gate and saves lightweight registration state. API batches
save every 25 successes and at the end. Periodic/final resume checkpoints remain.
Socket creation no longer scans all tracked sockets on every connection;
closed-socket pruning is throttled to once per second during activity.

### Medium: automatic port cycling could abandon a forwarded port

Zero download rate is not proof that a port is closed. Random local ports are
not automatically forwarded by PIA or another provider. A configured preferred
port now stays fixed during automatic stall handling. Automatic cycling is
also suppressed for SOCKS5 or unavailable torrent networking. Manual cycling
remains explicit. Update the preferred value when the provider changes it.

## Remaining Work

- **DHT validation:** v2.2.3 removes the independent bootstrap DNS path and
  binds DHT UDP/DNS through the VPN policy, with bounded queues and peer records.
  Local protocol and Windows source-bind tests pass; real PIA/Nord/provider
  packet-capture/drop/reconnect validation remains necessary. SOCKS5 DHT/UDP and
  protected IPv6 remain unsupported. LSD/router mapping stay disabled.
- **Slow service I/O:** PostProcessor performs extraction and engine moves
  synchronously under its monitor lock; ArrNotifier performs synchronous HTTP
  from the runtime tick. These can delay UI publication and queue admission,
  although existing MonoTorrent transfers run independently. Move them to
  bounded workers with safe shutdown and immutable snapshots, not fire-and-forget
  tasks. Recovery pause/remove actions can also delay that tick.
- **Completion and byte accounting:** the post-processor's legacy 99.9% cutoff
  and the per-file downloaded-byte estimate need stricter verified-completion
  and shared-piece boundary handling before treating them as exact. Queue
  classification now uses selected-piece completion rather than that estimate.
- **Download-to-download fairness:** the existing reserve protects downloads
  from ordinary seeds, not from another download occupying the global budget.
  Explicit force-start can consume headroom. A per-download fairness policy
  needs live tests and must avoid repeatedly disconnecting productive peers.
- **Slow-queue policy:** a metadata/no-peer job still occupies a configured
  active download slot. Optional timed rotation or separate metadata slots
  would help blocked queues, but must not silently override private-tracker
  expectations, force-start or user priority.
- **Evidence needed:** provider packet capture during VPN drops/reconnects,
  real public/private tracker behavior, physical x64 hardware and multi-day
  mixed-library load. These changes do not diagnose a kernel/whole-PC restart
  and cannot guarantee peers possess metadata or requested pieces.

## Verification

New `DiscoveryTests` use a controllable clock and disposable local network
fixtures, never production profiles/downloads. They exercise metadata health,
byte-level progress, backoff/toggle, callback lock separation, UDP-only DNS,
truncated UDP/TCP DNS fallback, fail-closed DNS, tracker failure/interval
semantics, actual magnet metadata plus hash-verified payload transfer,
same-tier failover, merged-tracker persistence and selected-file queue release.
See [NATIVE_DESKTOP_VALIDATION.md](NATIVE_DESKTOP_VALIDATION.md) for run results.
