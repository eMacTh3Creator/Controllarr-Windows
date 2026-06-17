# Performance Guide

This document covers the performance and scaling work in Controllarr for Windows
v2.1.15, which aligns the Windows port 1:1 with macOS Controllarr v2.1.15. The
behavior mirrors the macOS guide, described here in terms of the MonoTorrent
engine that powers the Windows build.

## Goals

The scaling target is straightforward:

- keep Controllarr responsive with very large libraries, including 1,000+
  torrents
- use available CPU cores when there is genuinely parallel work to do
- avoid turning the app into a memory or background-CPU hog just because the
  WPF window, bundled Web UI, or API is open

That does not mean every workload is identical. A library with 1,000 mostly idle
torrents is very different from 1,000 actively changing torrents on a slow disk.
The work here is about reducing Controllarr's own overhead so MonoTorrent, disk
I/O, and the network become the real bottlenecks.

## What Changed

### Shared Torrent Snapshot Cache

The `TorrentEngine` caches a short-lived torrent snapshot and derives session
totals from that same pass. This collapses several back-to-back MonoTorrent
scans into one shared snapshot when the runtime loop, HTTP API, bundled Web UI,
and native WPF UI all ask for state around the same time.

### Single-Pass Session Aggregation

Session totals such as download rate, upload rate, peer count, and torrent count
are derived from the cached torrent snapshot instead of triggering a second
scan.

### Parallel Service Ticks

The runtime performs one coordinated 2-second tick, but post-processing,
seeding-policy checks, and health-monitor analysis fan out concurrently after the
shared torrent snapshot is collected. This improves multi-core utilization
without duplicating polling work.

### Native UI Fast/Slow Refresh Split

The WPF window no longer republishes every snapshot every two seconds.
Fast-changing state such as torrents, session totals, health issues, and
VPN/disk status remains on the fast cadence. Slower operator state such as
categories, logs, recovery history, and settings refreshes less aggressively.

The system tray icon updates its status line (port, torrent count, transfer
rates) in place instead of rebuilding the whole menu every cycle.

### Web UI Active-Tab Polling

The bundled browser UI used to fetch nearly every table on every 2-second
refresh. It now always refreshes the live torrent/session summary, then fetches
only the active tab's heavier data set during normal live polling. Full
refreshes still load everything when needed.

### Conservative MonoTorrent Engine Tuning

The MonoTorrent `ClientEngine` is configured for balanced throughput rather than
aggressive maximum-resource operation:

- global and per-torrent **connection limits** are capped so a very large
  session does not open an unbounded number of peer connections at once
- hashing/recheck work is kept conservative so disk rechecks do not starve the
  rest of the engine
- the intention is steady throughput and less backpressure under load, not
  saturating every core and socket

### Bounded Tracker/DHT Pressure (Queueing and Discovery)

Torrent **queueing is left off by default** so the engine does not auto-pause
torrents in a large library; instead, tracker, DHT, LSD, and recheck pressure is
bounded internally:

- the number of torrents announcing to trackers concurrently is kept
  conservative, and concurrent HTTP tracker announces are limited
- failed trackers back off more aggressively
- **DHT**, **PEX (peer exchange)**, and **LSD (Local Service Discovery)** are
  available as engine toggles; LSD background work in particular is reduced for
  very large sessions where local discovery adds little but costs announce
  traffic

### Automatic Conservative Mode for Very Large Sessions

For very large sessions — roughly 650+ torrents during resume loading or polling
— Controllarr automatically enters a conservative mode:

- it lowers active tracker and DHT announce limits
- it disables LSD background work
- it reduces concurrent HTTP announces
- it spreads mass reannounce operations much more slowly

Mass reannounce operations are staggered over a longer window so a port cycle
does not ask every tracker for every torrent to announce at the same instant.
This keeps a large library stable through port cycling instead of triggering a
flood of simultaneous tracker/DHT work.

## Practical Guidance

For the best results on large libraries:

- run the published self-contained `Controllarr.exe` (Release build) when
  testing scale, not a Debug build
- keep the native window closed and run from the tray on always-on servers when
  the dashboard is not needed
- leave detailed peer/tracker views for active troubleshooting rather than
  permanent monitoring
- avoid keeping noisy log views open on multiple browser sessions unless you
  need them
- keep completed media on reasonably fast local storage when possible; SMB or
  external USB volumes will dominate post-processing time

## What To Watch

If you are validating a big migration or a 1,000+ torrent environment, check:

- CPU in Task Manager (or Resource Monitor) while the app is idle versus while
  torrents are actively changing
- memory growth over time with the window open and with browser sessions
  connected
- responsiveness of the Torrents tab and API under normal polling
- whether disk pressure or VPN rules introduce secondary pauses that look like
  performance issues
- whether DHT/PEX/LSD discovery is doing more work than your trackers require —
  disabling LSD on a large private-tracker library can cut background traffic

## Current Boundaries

This work removes a lot of avoidable overhead, but some workload-dependent limits
remain:

- tracker and peer detail views are intentionally on-demand and still do real
  work for the selected torrent
- very large browser sessions still pay the cost of rendering large tables
- storage speed and tracker/peer churn can dominate the total cost once
  Controllarr's own polling overhead is reduced

If you hit a real-world scaling wall, the next step is profiling that specific
workload rather than guessing. Use Task Manager, Resource Monitor, and the
persistent log at `%AppData%\Controllarr\logs\controllarr.log` to identify
whether the hot path is WPF UI invalidation, Web UI rendering, MonoTorrent
polling, or disk/network behavior.
