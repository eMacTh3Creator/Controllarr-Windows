# Native Desktop Validation

Date: October 8, 2026. Current release: `2.3.0`. Historical original candidates:
`2.2.0-preview.1` and `2.2.0-preview.2`. Baseline results below refer to
preview.1 unless noted; preview hashes are preserved as historical evidence.
Host: macOS cross-build; test target: the user's running Windows 11 ARM64
Parallels VM (10 virtual CPUs, 24 GB RAM). All engine fixtures and the live app
use disposable lab folders, never the user's production profile.

## v2.3.0 Remote and Import Prechecks

The final VM engine runner passed 248 checks, including explicit flat-layout repair,
repair idempotence, metadata gating, distinct Sonarr content/base paths,
per-add layout overrides, real loopback metadata/payload downloads and a real
1,000-torrent engine fixture and shared remote/desktop state mapping. The
paused/offline fixture imported in 4.70 seconds, uncached polling took 3.63 ms,
and runner private bytes were 53.7 MB. These are
not measurements of 1,000 simultaneous WAN downloads or desktop process RSS.
The package pipeline also passed native XAML/selection and profile-import tests.
Both exact x64 and ARM64 ZIPs passed authenticated Remote Protocol 1, bounded
events/pages, missing-VPN DHT denial, LAN control-plane responsiveness, force-start
guarding, live limits, encrypted-secret/settings restart, queue reset and
persistent batch-removal checks. Both Setup.exe packages passed install,
profile-preserving upgrade, optional older-profile import with backup and
uninstall while retaining metadata. All four SHA-256 values were checked after
copying the packages to the build host. x64 ran under Windows-on-ARM emulation,
not physical Intel/AMD hardware. No production profile was used. Historical
results below remain historical and are not substituted for new verification.

## v2.2.4 Storage Prechecks

The Windows 11 ARM64 storage/load source run passed **232 logic/real-engine checks**
(including optional 2,376-removal and 1,000-torrent fixtures).
New coverage verifies single-file and multi-file category wrapping, explicit
base-path overrides, global/category opt-out precedence, nameless magnet
folders, trailing separators, non-flattening completed moves, collision refusal,
manual pause preservation, repeated-move idempotence and real engine restart.
Legacy flat torrents are not reorganized when a checkbox changes. Archive
fixtures prove selected-only extraction in a shared root, full-completion and
metadata guards, and failed-move reporting. A subsequent mixed-case category
hash fix passed its regression check and the final default **230-check** Windows
source suite. The final portable macOS runner passed **133 checks**; it does not
run Windows engine fixtures.

The WPF harness passed all 13 pages, 10,000-row viewport virtualization and
native category/default checkbox bindings, including opted-in new categories.
A local tracker/peer fixture passed actual nameless magnet metadata and verified
payload transfer inside its stable folder, then restored that data after restart.
The final packaging run repeats the default source suite, WPF harness and profile
import safeguards. WebUI storage/queue/DHT round-trip, website downloads and
JavaScript syntax checks passed. Final package checks are recorded below only
after execution; source builds alone do not establish package status.

The optional local engine fixture imported 1,000 torrents in 4.46 seconds and
averaged 4.64 ms per uncached poll, with 73.3 MiB runner private bytes at sampling.
This is not whole-app peak memory or a production network/disk throughput test.

The prior networking limitations remain: physical x64 hardware, real VPN-provider
packet capture, production SMB/path mappings and long-duration large-payload
load are not certified by these disposable local fixtures.

### Final v2.2.4 Package Checks

Both exact x64 and ARM64 ZIP payloads passed isolated API startup, category-folder
intake without a trailing backslash, captured-path/settings restart, category
policy changes without reorganizing existing files, mixed-case category
reassignment, missing-VPN DHT denial, live peer limits and persistent deletion.
Both Setup.exe packages passed complete-payload install, keep-existing profile,
in-place upgrade, optional previous-profile import with backup, and uninstall
while retaining metadata. Final packaging repeated the 230-check default engine
suite, WPF checkbox/virtualization tests and profile-import safeguards.

All four final artifact hashes match
[SHA256SUMS.txt](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.4/SHA256SUMS.txt),
both ZIP CRCs passed, and the archives include the storage guide, release notes
and third-party notices. The x64 payload runs under Windows-on-ARM emulation,
not physical x64 hardware. No production profile/downloads were used and public
firewall access was not granted. The bundled report records pre-packaging
checks; this online section adds the subsequent exact-package results.

## v2.2.3 DHT Prechecks

The Windows 11 ARM64 VM source run passed **203 logic/real-engine checks**
(including optional bulk-removal and 1,000-torrent fixtures),
including real local UDP DHT bootstrap/get_peers, cache restart, incoming
token-validated announcements, advertised peer ports, spoofed-source rejection,
bounded bencoding depth/size, invalid-token allocation denial, missing-VPN
DNS/socket denial, proxy refusal, blocklist destinations and bootstrap shutdown
cancellation. Provider-name checks cover PIA/NordLynx and generic/popular tunnels,
including negative physical/Hyper-V/substring matches.

Windows DHT datagrams were checked against an explicitly selected guest adapter:
listener/send source binding, reset/rebind, and immediate topology-latch closure
passed. That guest adapter is not an encrypted provider tunnel. WPF again
passed all 13 pages, filter/selection/layout and 13 realized rows for 10,000
items. The macOS portable logic/DHT run passed 123 checks; it does not validate
Windows interface options. Real-provider packet capture, physical x64 hardware
and multi-day churn/throughput remain unverified.

Five consecutive final-source runs passed all 118 discovery/network checks.
Repeat testing exposed an HTTP tracker failure-reason response marked OK by
MonoTorrent; the adapter now rejects it for tier failover, with a dedicated
regression check. The original random 16-node DHT fixture occasionally missed
the ten-node readiness threshold; the final fixture uses 64 local UDP nodes.
WebUI checks cover DHT/adapter settings preservation and saved dropdown values
after options are populated; site and JavaScript syntax checks also passed.

The ARM64 source runner measured 2,376 paused local 16 KiB removals in 36.25
seconds and eight hung-tracker removals in 2.06 seconds. Its 1,000-torrent import
took 4.21 seconds; uncached stats polling averaged 3.65 ms. Runner private bytes
were 66.0 MiB at measurement, not whole-app or peak memory. These disposable
local fixtures do not measure large-payload/network throughput.

### Final Package Checks

The exact v2.2.3 x64 and ARM64 ZIP payloads both passed isolated network/API
startup, missing-VPN DHT diagnostics, live limits, topology latch, encrypted
credential restart, metadata pause, queue-rank repair and persistent deletion
checks. Both Setup.exe installers passed complete-payload install, keep-existing
profile, in-place reinstall/import with backup and uninstall while retaining
profile metadata. Packaging also reran the default 200-check engine suite,
WPF harness and profile-import safeguards. All four artifact SHA-256 values and
both ZIP CRCs were verified on the build host; the archives contain the correct
x64/ARM64 executable payloads, guides and third-party license notices.

x64 ran under Windows-on-ARM emulation, not physical Intel/AMD hardware. No
production profile or downloads were used. Public-network firewall access was
not granted and firewall protections were not disabled for these checks. The
release's [SHA256SUMS.txt](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.3/SHA256SUMS.txt)
identifies the tested binaries. The bundled report records pre-packaging source
checks; this online section records the subsequent exact-artifact checks.

## Discovery Audit Before DHT

Source after v2.2.2 passed the Release cross-build and 163 logic/real-engine
checks in the Windows 11 ARM64 VM. The new coverage includes actual loopback
HTTP-tracker magnet metadata acquisition and a hash-verified 64 KiB payload,
same-tier failover after a rejected announce, tracker interval/minimum handling,
merged tracker/pause restart persistence, selective-file queue-slot release,
metadata health/backoff/toggle and byte-progress detection. Local DNS fixtures
verify UDP-only responses, truncated UDP to TCP fallback, and missing-VPN denial.
The WPF harness passed all 13 pages, selection/layout/filter preservation and
viewport virtualization (13 realized rows for 10,000 items). These are source
prechecks, not a published release, real-provider leak certification or a
production throughput/metadata-availability guarantee. See [STALL_AUDIT.md](STALL_AUDIT.md).

Final source also published self-contained x64 and ARM64 app folders to a new
disposable VM lab directory; both passed isolated API/network checks (including
missing-VPN starts, live limits, topology restart latch, encrypted credentials,
metadata pause, deletion persistence and queue repair). The self-contained x64
test runner passed 166 checks with both optional large fixtures enabled. x64
ran under Windows-on-ARM emulation, not physical Intel/AMD hardware.

The x64 runner measured 2,376 paused 16 KiB fixture removals in 52.08 seconds
and eight hung-tracker active removals in 2.10 seconds. Its subsequent 1,000
local-torrent run imported in 4.63 seconds and averaged 6.31 ms per uncached
stats poll. Runner private bytes were 172.1 MiB at measurement, after the large
removal suite; this is not a clean-start or whole-app memory measurement and
is not directly comparable to the earlier ARM64-only run. Node WebUI/site
checks and the final zero-error cross-build also passed (17 existing warnings).
All fixtures were isolated; no production profile/downloads were accessed and
no release assets or published website were changed during that earlier audit.

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
