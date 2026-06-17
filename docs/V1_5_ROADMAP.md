# Controllarr for Windows — Roadmap (Historical Product Direction)

> **Status note.** This document is kept as historical / product-direction context. It was adapted from the macOS Controllarr v1.5 roadmap for the Windows port. The shipping Windows build is **v2.1.15**, which is aligned 1:1 with macOS Controllarr v2.1.15, so several of the foundations and epics described below have already landed. Treat the themes here as the direction that shaped the product, not as an outstanding commitment.

## Vision

This release line is where Controllarr for Windows stops feeling like "a nicer qBittorrent for Windows" and starts feeling like a dedicated media-download orchestration platform for Sonarr, Radarr, Overseerr, Plex, and power users who want reliability without running a full Linux stack.

The Windows port already has the right foundation:

- a native WPF UI (Home dashboard, Torrents, Categories, Settings, Health, Recovery, Post-Processor, Seeding, *arr, Log)
- a bundled browser-facing Web UI served by the embedded ASP.NET Core Kestrel server
- qBittorrent Web API v2 compatibility
- a MonoTorrent (pure .NET) torrent engine
- category-aware post-processing with SharpCompress archive extraction
- seeding policy and health monitoring
- detail panes for files, trackers, and peers
- DPAPI-protected credentials and VPN-aware kill switch / interface binding

The next big jump is not just adding more toggles. It is adding systems that make Controllarr proactive, explainable, remotely operable, and safer to trust as the center of a media pipeline.

## Foundations Already Started

The first roadmap-oriented pieces are now in the shipping build on `main`:

- a self-contained, no-install `Controllarr.exe` (win-x64) suitable for headless / always-on deployments
- Web UI backup export and restore workflows
- optional secret export for the Web UI password (DPAPI-backed) and saved *arr API keys (stored in the app-state file)
- a first-pass health-based recovery engine with automatic/manual action logging
- operator-facing post-processing retries and explicit disk-space rechecks in the Web UI
- a persistent, crash-surviving on-disk log at `%AppData%\Controllarr\logs\controllarr.log` with fsync-on-warn/error and ~5MB rotation

Those are intentionally infrastructure-heavy changes: they make later work on remote operation, admin features, and reliability much easier to ship cleanly.

## Release Themes

### 1. Download Orchestration

Make the MonoTorrent engine policy-driven instead of mostly reactive.

Feature candidates:

- Rule engine for torrent lifecycle events
- Queue profiles for different content types
- Smart auto-tagging based on tracker, category, source app, or filename
- Tracker policy groups with failover behavior
- Automatic recheck / reannounce / pause / delete playbooks
- Batch actions across filtered torrents
- Cross-seed and duplicate-content detection
- Per-category or per-tracker queue budgets
- Download windows and "quiet hours" beyond the existing bandwidth scheduler

Why it matters:

- This moves Controllarr from a client UI into a workflow controller.
- It reduces the amount of manual cleanup Sonarr/Radarr users still do after a torrent is added.

### 2. Deeper *arr Integration

The current qBittorrent compatibility is a strong base, but the next release should make Controllarr feel intentionally built for the *arr ecosystem.

Feature candidates:

- Sonarr/Radarr import-readiness checks before a download is considered "healthy"
- Re-search policies based on stalled health state, bad trackers, low availability, or failed import (building on the existing *arr re-search integration)
- Per-app templates for categories, save paths, seeding rules, and blocked file types
- Manual approval inbox for suspect downloads before they continue
- Richer webhook ingestion from Sonarr, Radarr, and Overseerr
- Download lineage view: request -> add -> download -> post-process -> import
- Library-aware rules for anime, movies, UHD remuxes, season packs, and music
- Hardlink / move validation before signaling completion downstream

Why it matters:

- Users do not just want torrents to complete.
- They want media to land in the right place, import cleanly, and self-heal when the pipeline breaks.

### 3. Reliability, Recovery, and Safety

This release line should make Controllarr dramatically better at surviving the ugly real-world failure cases.

Feature candidates:

- Health score per torrent instead of only discrete issue rows
- Disk pressure manager with graduated policies instead of a single pause threshold
- Post-processing retry queue with quarantine states and operator notes
- Resume-data and state snapshot backups (MonoTorrent fast-resume sidecars + periodic `state.json` saves)
- Backup / export / restore for settings, categories, and history
- Recovery center for "why is this stuck?" diagnostics
- VPN policy profiles: hard stop, soft pause, or interface migration across TAP-Windows / WireGuard / Wintun adapters
- Startup integrity checks for library paths, NTFS permissions, free space, and missing tools
- Automatic "safe mode" launch after repeated crashes, aided by the crash-surviving on-disk log

Why it matters:

- Reliability is the feature that converts a clever tool into a daily driver.
- A lot of user trust comes from being able to explain and recover from failures.

### 4. Remote and Distributed Operations

Today Controllarr for Windows is a local WPF app with a bundled Web UI. This release line could make it usable as a real control plane.

Feature candidates:

- Headless / service mode for always-on systems (e.g. a Windows Service or auto-start tray-only deployment)
- Remote-node support so one Windows UI can manage multiple Controllarr instances
- WebSocket push updates instead of full polling everywhere
- Mobile-friendly remote dashboard
- Push notifications for failures, import-ready events, or VPN drops
- Tray-only deployment mode for tiny always-on hosts
- Lightweight remote agent installer / pairing flow
- Multi-machine handoff of completed downloads

Why it matters:

- A lot of serious *arr users run mixed environments.
- Remote operations massively expand who can use Controllarr and how often they keep it open.

### 5. Security and Administration

The more Controllarr can run remotely or in shared environments, the more important this becomes.

Feature candidates:

- Multi-user accounts with roles
- Read-only operator mode
- Scoped API tokens for apps and automation
- Full audit log of user and automation actions
- Better session management with expiry, revocation, and device history (building on the 1h sliding-expiry session auth)
- Secure secret storage for *arr keys, tracker credentials, and external integrations (only the Web UI / API password is DPAPI-backed today)
- Network access controls and trusted-origin controls for the Web UI (building on the existing security headers, optional clickjacking protection, and CIDR IP allowlist)
- Encrypted config export for backups

Why it matters:

- Security becomes a product feature once Controllarr moves beyond localhost-only usage.
- Admin features also make the product more supportable and easier to reason about.

### 6. UX and Operator Experience

This release line should feel faster and more "control-room" oriented, not just larger.

Feature candidates:

- Global search and command palette (extending the new Home dashboard and the Torrents name/category/info-hash search box)
- Saved filters and custom dashboard views
- Activity timeline across adds, health events, post-processing, and imports
- Queue analytics and session charts
- First-run setup wizard for folders, qBit-style creds, and *arr pairing
- Better empty states and explanations for unhealthy torrents
- Inline action history on torrents, categories, and health issues
- Keyboard-first workflows in the native WPF app
- Better responsive layout for the Web UI on tablets and phones

Why it matters:

- A tool that surfaces more power also needs to surface more clarity.
- Operator speed matters if this becomes a "leave open all day" app.

### 7. Extensibility and Ecosystem

This is how Controllarr stops having to ship every integration directly in the core app.

Feature candidates:

- Webhook engine for outgoing events
- Script hooks or actions for advanced operators (e.g. PowerShell post-processing hooks)
- Plugin architecture for integrations and custom policies
- Public REST plus WebSocket management API
- Import/export for templates, rule sets, and category profiles
- Metrics endpoint for Prometheus / Grafana style monitoring
- Event bus or action stream for external automation tools

Why it matters:

- Extensibility makes the app more future-proof.
- It also lowers the pressure to solve every niche request in the core UI.

## Recommended Scope

If the goal is a big but coherent release, it should probably center on these six epics:

1. Rule engine and recovery playbooks
2. Deeper Sonarr/Radarr/Overseerr integration
3. Headless / service mode plus better remote Web UI
4. Health scorecards and recovery center
5. Multi-user auth, audit log, and API tokens
6. Backup/restore plus the GitHub-release update flow

That bundle makes the release feel materially different without exploding into a multi-year rewrite.

## Stretch Goals

These are compelling, but they should only land if the core six epics above are already stable:

- Multi-node fleet management
- Plugin SDK
- Mobile push notifications
- Cross-seed assistance
- Import approval inbox
- Metrics / observability dashboards

## Features That Would Feel "Huge" In Marketing

If the goal is also to make the release easy to explain publicly, these are the most headline-worthy:

- "Controllarr can run as a local Windows app or a headless media-download server."
- "Rules and playbooks automatically recover unhealthy torrents."
- "Controllarr understands Sonarr/Radarr import workflows, not just torrent states."
- "You can securely manage multiple nodes and operators from one UI."
- "Built-in backup, restore, updates, and audit trails make it production-friendly."

## Update Mechanism

The macOS app uses Sparkle with a signed appcast for in-place updates. The Windows port intentionally does **not** ship a silent in-place updater. Instead it checks the **GitHub Releases API** for a newer tag:

- a "Check for Updates" action in the native WPF app and a settings toggle to enable/disable the check
- when a newer release is found, Controllarr opens the latest GitHub release page rather than downloading and installing silently
- updating is a deliberate user action: download the new self-contained `Controllarr.exe`, unblock the mark-of-the-web (right-click → Properties → Unblock, or `Unblock-File` in PowerShell), and replace the old binary

This keeps the unsigned-binary distribution model honest about what is happening and avoids shipping an auto-installer that would trip Windows SmartScreen in surprising ways. A future roadmap item could add an assisted download-and-swap helper while keeping the user in control of the actual replacement step.

## Suggested Documentation Follow-Ups

If this roadmap turns into further implementation work, the next docs that should exist are:

- architecture overview
- API reference (qBittorrent v2 surface + Controllarr-native endpoints)
- operator guide
- remote deployment guide
- security model / auth guide
- backup and restore guide

## Summary

A strong release would make Controllarr for Windows feel less like a client and more like a media-operations product. The biggest wins are automation, remote control, reliability, and deeper *arr awareness. If those land cleanly, Controllarr becomes something much harder to replace with a generic torrent app.
