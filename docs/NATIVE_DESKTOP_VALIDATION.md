# Native Desktop Validation

Date: October 7, 2026. Current release: `2.2.2`. Historical original candidates:
`2.2.0-preview.1` and `2.2.0-preview.2`. Baseline results below refer to
preview.1 unless noted; preview hashes are preserved as historical evidence.
Host: macOS cross-build; test target: the user's running Windows 11 ARM64
Parallels VM (10 virtual CPUs, 24 GB RAM). All engine fixtures and the live app
use disposable lab folders, never the user's production profile.

## v2.2.2 Source Prechecks

Release cross-build passed with zero errors. Windows VM logic/real-engine tests
passed 132 checks (134 with the optional 2,376-torrent removal fixture). New
coverage includes download-first total-cap admission, explicit zero-cap reasons,
1,000-seed bounded connection planning, forced/lower cap accounting, reserve
validation/migration, live reason notifications without filter resets, and
unknown tracker counts. Real-engine fixtures check single/bulk/cancelled queue
compaction, legacy restart repair, unknown option probes, and rank 1 after
emptying the library. Adding after 2,376 deletions uses the remaining live count.

The connection test establishes two real loopback BitTorrent peer handshakes,
then confirms a tightened seed budget releases existing peers. A third peer
checks that retained paused sockets close on the next scheduler pass without
resuming that torrent. Seed caps restore after pausing the last metadata job,
without rewriting saved overrides. These are controlled peers, not an internet
swarm or physical VPN test. The disposable 2,376-torrent removal run took 37.52
seconds; a previous candidate measured 38.23 seconds. No real storage/network
throughput guarantee is inferred.

WPF tests passed all 13 pages, selection/filter/layout, the live selected-row
diagnosis binding and 13 realized rows for 10,000 items. Node tests passed WebUI
queue/connection settings round-trip and unrelated field preservation, status
normalization, trusted architecture-specific download links and offline fallback.
Packaging reruns build, engine/WPF and profile-import safeguards. Installer/API
checks use test-only identities, profile folders and loopback ports; production
settings/torrents are not used. Historical results below remain unchanged.

## v2.2.1 Prechecks

Release source builds passed on the macOS cross-build host and Windows VM.
The default suite passed 106 logic/real-engine checks (107 with the large-removal
fixture enabled); the WPF harness passed
all 13 pages, selection/filter/layout checks and viewport virtualization
(13 realized rows / 10,000 items). Removal tests cover pause-all before deletion,
bounded worker concurrency, duplicates, partial failure reporting, cancellation,
blocked resume/force-start, metadata-stage cancellation, keep-files behavior,
payload deletion, restart persistence and moved-cache restoration. A stale
removed metadata entry is skipped without discarding the remaining library.

Controlled removal measurements on this VM:

- 2,376 paused local 16 KiB torrents and payload files: 37.37 seconds on the final source (37.27 seconds on the earlier candidate).
- Eight active fixtures with a local HTTP tracker accepting but never replying:
  2.06 seconds, with successful removal of all eight.

Neither measurement is a large-payload, real-peer or SMB throughput guarantee.
The site test covers direct installer/ZIP URLs per CPU and safe API-failure
fallbacks. Profile-import tests cover settings/categories, same-user DPAPI,
metadata-only copying, backups, validation without mutation, skipped passwords
with loopback-only WebUI, rejection of malformed JSON/undecryptable credentials,
read-only backup handling, and rollback after a locked target file prevents replacement.

Both x64 and ARM64 installer candidates passed isolated silent install,
keep-existing-profile, in-place reinstall/import-with-backup and uninstall
checks. Complete payload folders were installed; profile metadata survived
uninstall. Test-only identities, app directories and profiles were used, with
no production shortcuts/profile modified. Installers and app binaries are unsigned.

Historical v2.2.0 results follow. Real VPN provider packet capture, physical
x64 hardware tests, multi-day load, and proof of the older ARM64 crash's root
cause remain outside these prechecks.

## v2.2.0 Source Prechecks

Fresh v2.2.0 Release builds passed on the macOS cross-build host and the Windows
VM with zero errors (one warning on the incremental build). All 88 logic/real-
engine checks passed. The WPF harness loaded all 13 pages, realized 13 rows for
10,000 items and passed selection/filter/layout tests. The public-site Node
test passed architecture-specific ZIP selection, direct-download fallbacks
when the GitHub API fails, trusted asset URLs and local asset/doc targets.

Packaging reruns the engine/WPF suites and produces complete app folders for
both architectures. Release SHA-256 checksums are published in `SHA256SUMS.txt`;
the preview hashes below do not identify the release ZIPs. See the final
package precheck section for exact-binary API/restart results. Full real-VPN,
physical x64 hardware and long-duration load testing is not claimed.

## v2.2.0 Package Prechecks

Both self-contained release ZIPs were extracted into fresh lab folders. Both
app hosts report product version `2.2.0`. Each passed the isolated network/API
test: authenticated WebUI/API available on loopback with the configured VPN
missing; add/resume/force-start blocked; topology changes closed until restart;
advanced controls and the DPAPI proxy password restored after restarting;
no proxy password present in plaintext `state.json`.

Each architecture also passed two restart/login/browser-index cycles with
250 authenticated torrent-list requests per cycle (500 per architecture),
the same three paused fixtures and graceful shutdown. These are short VM
prechecks, not a soak or a real-provider/LAN leak test. The final packaging
pass includes this report and the corrected operations/install/networking
guides; the published checksum file identifies the actual release archives.

## Passed

- Release compilation on macOS with Windows targeting and in Windows.
- 51 catalog/bulk/real-engine checks, plus the earlier opt-in `--engine-load` run.
- Immutable row identity, changed-property notifications, combined filters,
  case-insensitive hash deduplication, batch cancellation and partial failures.
- 1,000/10,000-row catalog reconciliation, with no collection reset over
  50 unchanged polls. Latest measurements: about 0.40/2.84 ms per poll.
- Actual WPF layout: 13 realized rows out of 10,000 items; multi-selection
  survived unchanged and state-changing polls. Category filtering dropped
  hidden selections; empty selections disabled destructive commands.
- All thirteen native page templates loaded, including RSS.
- Column width/order persistence round-trip passed in an isolated temporary
  layout profile; the WPF harness does not access the production profile.
- Real queue enforcement, force-start and queue ordering; duplicate import
  preservation; per-torrent speed, tracker and file-priority persistence.
- Creation of valid metadata from real local fixture files and paused migration
  imports. RSS/Atom parsing, regex filters and XML external-entity rejection.
- Real local file verification, storage moves, remove-only preservation, file deletion on
  disposable payloads, torrent-list restoration and paused-state persistence.
- Opt-in engine load: 1,000 tiny local torrents, 4.75 seconds import,
  3.13 ms average uncached stats poll across 30 polls. Runner private bytes
  were 36.1 MB at the measurement point, not the whole desktop's memory use.
- Live app launch, authenticated qBittorrent API, bundled browser index and
  adding a local fixture through the API. The rebuilt ARM64 app survived
  500 repeated torrent-list API requests; the x64 package also ran in the VM.
- Graceful shutdown of the isolated app through its authenticated API.
- Interactive native Add Magnet shortcut, right-click menu, file inspector,
  category-scoped Ctrl+A and single/bulk Delete-key dialogs (cancelled, no
  deletion performed). Native Resume succeeded and bulk Pause reported two
  successes. Changing category removed hidden selections and disabled actions.
- Final dark File menu and removal-radio styling. Keep downloaded files was
  visibly selected; pressing Enter cancelled the bulk removal dialog, with
  both torrents still registered.
- Explicit hide-to-tray, tray menu restoration, normal taskbar minimization
  and restoration, and close-to-tray followed by tray-menu restoration.
  The engine continued running while its window was hidden.
- Final self-contained x64 package: three restart/login/browser-index cycles,
  500 authenticated torrent-list polls per cycle, and the same three fixture
  hashes restored each time. All fixtures remained paused. The final x64 run
  used only the lab profile. Earlier three-cycle
  idle-before-login loops also survived; a Windows PowerShell 5.1 array-wrapping
  issue in the test reporter was corrected before the final identity checks.

## Issues Found and Fixed

WPF collection refresh during deferred changes could throw; reconciliation
now batches structural notifications in the catalog. WPF current-item
synchronization could select a new row after filtering; it is disabled, and
selection is pruned to the current query. A duplicate PasswordBox style was
removed. MonoTorrent's startup/stop race could dispose its stopping token;
the wrapper now retries that specific race, with repeated fixture runs passing.
Stop operations also wait for short asynchronous startup initialization before
changing storage or unregistering a torrent. Storage moves stop the torrent first, and manually paused torrents are
persisted separately from engine shutdown state.

## Outstanding Validation and Historical ARM64 Issue

Two ARM64 runs terminated with an AccessViolationException in ASP.NET
EndpointMiddleware during an API request, including the final rebuilt
candidate during login after the interactive checks. Windows recorded
exception `0xc0000005` and .NET runtime `8.0.31` at 15:37:50 local time.
Earlier 500-request loops and stable-SDK rebuilds did not resolve this
intermittent issue. ARM64 remains experimental; a root-cause fix is not claimed.
The release owner accepted publication based on prechecks with this limitation
disclosed, rather than waiting for a full hardware/VPN/load test matrix.

The packaging was changed to self-contained **complete app folders** for both
architectures, instead of ARM64 single-file extraction. An initial normal
ARM64 folder run passed five rounds of login/100 polls with 60-second idle gaps
(approximately five minutes). The expanded x64 folder candidate passed three
restarts, 500 authenticated polls each, browser-index access and restoration
of the same three paused hashes. These are short checks, not proof that the
intermittent ARM64 crash's underlying cause is fixed.

The expanded ARM64 folder candidate additionally passed five restart/login/
browser-index cycles with 500 authenticated torrent-list requests per cycle
(2,500 polls total), 30-second idle periods and the same three paused fixtures.
ReadyToRun was left at its normal setting. No crash occurred in those cycles.

Both final ZIPs were then extracted into fresh `package-win-x64` and
`package-win-arm64` lab folders. Each exact packaged binary passed two additional
restart/login/browser-index cycles with 250 authenticated polls per cycle
(500 per architecture), unchanged fixture hashes and graceful shutdown.
Both ZIPs passed archive integrity checks; PE headers identify x86-64 and
AArch64 respectively. Checksums are in `publish/packages/SHA256SUMS.txt`.

Final VM interaction attempts could not reliably deliver clicks/keys even to
the Windows Start button through the Parallels automation surface. Do not count
the new native dialogs/RSS page as manually verified from those attempts;
automated WPF layout tests and live API/process checks are separate evidence.

An isolated ARM64 diagnostic launch with `DOTNET_ReadyToRun=0` survived ten
logins and 1,000 polls. Five additional fresh runs passed five logins and
500 polls each: two with default settings, three with ReadyToRun disabled.
Because the normal runs also passed, this is not a proven workaround or
root-cause identification. No global runtime settings or security settings
were changed; these were process-local diagnostics. Microsoft's
[compilation configuration documentation](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation#readytorun)
describes the diagnostic switch. Preview SDKs are excluded by `global.json`,
but that alone does not fix the observed crash.

`scripts/test-windows-api.ps1` now supports repeated isolated restart/login/
browser-index/poll/shutdown cycles, idle time before requests, and an optional
ReadyToRun diagnostic switch. Passing this test is still not a long-duration
soak or evidence of production throughput.

Windows firewall prompts initially blocked direct interaction; after they
cleared, the checks listed above were completed. Broad inbound access was
not granted. Still manually verify taskbar thumbnail actions, tray double-click
restoration, file-priority persistence and category/settings saves. The custom
tray tooltip/menu also needs another appearance pass. The WPF harness checks real controls/layout but is
not a replacement for these end-user interaction checks.

Validate real downloads/seeding, thousands of mixed-size multi-file torrents,
sustained peer churn, slow/network storage, VPN drop/reconnect, resource use
when minimized and with remote Sonarr/Radarr traffic. No claim of perfect
load behavior or exhaustive uTorrent/BitTorrent feature parity is made. Managed
queues, RSS/watch-folder intake, metadata migration, tracker editing, speed
limits and torrent creation were implemented in preview.1. Preview.2 additionally
implements sequential/streaming support and encryption/proxy/IP-blocklist
controls. Super-seeding and foreign verified-piece resume conversion remain
unsupported. New desktop dialogs and RSS network/
watch-folder operations need more end-user integration testing; parser tests
alone do not validate private tracker feeds. These preview checks preceded
the v2.2.0 release build.

The preview.1 audit found placeholder `BindToAddress` adapters. Preview.2
replaces that path with policy-owned peer/tracker/webseed sockets, selected
adapter pinning and fail-closed starts. It does not claim OS-wide firewall
enforcement or bypass provider LAN restrictions.

## Preview.2 Networking and Advanced Controls

The Windows 11 ARM64 VM passed 88 logic/real-engine checks, including actual
TCP source binding and `IP_UNICAST_IF`, UDP source binding, IPv6 refusal,
closing existing sockets, missing-adapter start guards, real HTTP/UDP tracker
factories, SOCKS5 remote DNS/authentication/no-fallback, DNS parser bounds,
live encryption/PEX and per-torrent connection/upload-slot settings, persistence
and a verified-file byte-range streaming preview with a capability URL.
Accepted peer reply sockets are interface-pinned, cancelled connects release
their sockets, and concurrent tuning/rate changes preserve encryption and
per-torrent controls. Atomic resume checkpoints restore verified progress.

Repeat testing exposed stalled `Starting` states after picker changes and
state saves. Library startup and state saves could write the same resume file
concurrently. The candidate disables those library-owned writes before restore
and uses compatible, atomic, serialized checkpoints with 30-second polling.
Five consecutive 84-check runs passed after this change; the subsequently
expanded 88-check suite passed during staging and packaging. This is not a
claim that the earlier ARM64 API AccessViolation has been root-caused.

The WPF harness again loaded all 13 native pages and realized 13 transfer rows
for 10000 items, with selection and saved-layout checks passing. These are
actual WPF controls, not evidence of manual interaction or real throughput.
The adapter used for socket-option tests was the guest's ordinary network
adapter, explicitly not evidence of an encrypted VPN tunnel.

The streaming test serves an already verified 16 KiB fixture. It does not
prove smooth playback while missing pieces download or codec compatibility.
Real-provider packet capture, DNS outage, adapter drop/reconnect, private
tracker traffic, provider firewall/LAN behavior, physical x64 hardware and
multi-day mixed-library load remain outstanding validation. See [NETWORKING.md](NETWORKING.md).

Both final preview.2 ZIPs were extracted into new lab folders. Each packaged
binary passed the isolated network/API test: missing VPN blocks add/resume/
force-start while authenticated WebUI/API stay available; topology changes
latch closed until restart; advanced options and a DPAPI-encrypted proxy secret
survive restart, with no proxy password in plaintext `state.json`. The test
does not expose a LAN listener; it validates the management server on loopback.
PowerShell 5.1 response decoding and the disk schema in the fixture were fixed
before these checks passed.

Each architecture additionally passed two restart/login/browser-index cycles
with 250 torrent-list polls per cycle and the same three paused fixture hashes.
An earlier preview.2 ARM64 package passed an additional 20-poll launch for the interactive check. The VM
firewall prompt would not accept automated Cancel clicks or Escape; no broad
firewall access was granted, and new manual dialog checks are not counted.

The final opt-in engine load run passed 89 checks including 1000 local fixture
torrents: 3.59 seconds import, 4.90 ms mean uncached stats poll, 42.3 MB runner
private bytes. These are tiny files without peers, not desktop memory or
real-network throughput measurements. Host cross-build succeeded with no
errors, and both copied archives passed integrity and SHA256 verification.

The final repack includes the desktop/networking guides, preview failure
cleanup and context-independent engine-settings gate continuations. Its 88
checks and WPF harness passed again. Both freshly extracted final binaries
also passed the network/API test and two 250-poll restart cycles per architecture.
Earlier preview.2 archives are preserved separately, not overwritten.

Historical local preview packages (not published) are in `publish/packages-preview2-final`:

```text
f5eff642a48269462c5d54801c175e805e1438dd529f106cb7be65a678bce6cc  Controllarr-2.2.0-preview.2-win-x64.zip
6d3c01f6837f48e1c4ca224e3ad9225df1bc1521ae607194e66e57358f5b90fe  Controllarr-2.2.0-preview.2-win-arm64.zip
```
