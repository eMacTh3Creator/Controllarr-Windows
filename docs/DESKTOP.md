# Native Windows Desktop

The redesigned desktop is WPF, not a WebView2 portal. It talks directly to
MonoTorrent and the runtime services. The browser WebUI and qBittorrent API
remain separate remote-management interfaces; no WebUI login is needed locally.
The current v2.2.3 release adds bound DHT/discovery reliability to repaired queue ranks, download peer headroom and
live transfer diagnosis to the full x64/experimental ARM64 installers,
profile preservation/import and pause-first bulk removal. See [INSTALL.md](INSTALL.md).

## Transfer Workspace

Transfers is the default page. Search matches name, category or hash; the status
dropdown combines with category navigation. Click column headers to sort.
Resize/reorder columns and drag the splitter above the inspector to adjust the
table/details balance. Column order, widths and sorting are saved on close/exit;
the inspector splitter remains session-only.

Ctrl-click toggles individual rows, Shift-click selects a range, and Ctrl+A
inside the table selects all shown torrents, not hidden categories/search
results. Right-clicking an unselected row selects that row; right-clicking a
selected row preserves the full selection. Polls do not replace row identity.

The toolbar, Transfers menu and context menu act on the selected rows. Pause,
resume, reannounce, verify files, assign category and move storage support
multiple torrents. Assigning a category changes the label; moving files is an
explicit separate operation. Verify files runs serially and leaves torrents
paused. Explicitly paused torrents stay paused after restart. Open folders is capped at ten distinct folders to avoid flooding
Explorer. Copy magnets supports v1 and v2 hashes.

## Safe Removal

Press Delete while the transfer table has focus, or choose Remove selected
from the toolbar/context menu. The dialog offers:

- Remove only from Controllarr, keeping downloaded files (default).
- Remove from Controllarr and permanently delete downloaded files.

The latter requires another confirmation and does not use the Recycle Bin.
Cancel is the default keyboard action in the removal dialog; Enter does not
silently remove the selection.
Check the selection and whether another application uses the files. Bulk
operations snapshot their target hashes; changing selection afterwards does
not change the active job. Cancel stops before the next torrent, not halfway
through an ongoing engine/disk operation. Partial failures appear in the status
bar, a summary dialog and Log.

Removal first pauses the entire selection, then overlaps up to 16 stop operations
with bounded final tracker waits. Progress shows pausing, stopping, then removal
or file deletion. Disk deletion stays serial. Cancel still finishes stopping the
selected set, but prevents further deletions; untouched items remain paused after
restart. Resume/force-start requests are rejected while targets are pending.
Periodic removal saves do not rewrite every remaining torrent's resume cache.
Remaining queue ranks compact once after the batch, including Cancel/partial
failure. A new torrent appends to the live library, not the lifetime-added count.
Startup also repairs gaps from older versions without changing relative order.

## Adding and Inspecting

Ctrl+M opens the native magnet dialog with category and optional save-folder
override. Ctrl+O opens a multi-file .torrent picker. Drop .torrent files into
the transfer workspace to add them to its current category/folder.

Select one torrent to inspect Files, Trackers and Peers. Files supports skip
and persistent priority selection with an explicit Apply button. Edit trackers
replaces the selected public torrent's URLs; private-torrent replacement is
blocked. Peer and tracker statistics are snapshots; Refresh details fetches a fresh view. Multi-selection
clears the single-torrent inspector to avoid ambiguous file edits.
The selected torrent's waiting explanation updates with normal polling; hover
Status for the same explanation. The status bar shows global connected peers /
limit. Peer/seed counts in the main table describe connected peers, not the entire
swarm. Trackers shows Unknown until scrape counts are available. Refresh details
to investigate tracker failures; no connected peers does not prove no seeders.

Category edits validate unique names, absolute paths and non-negative limits.
Saving a renamed category updates its assigned torrent labels; removing a
category and saving clears those labels without deleting files. Blocked-file
extension changes take effect on subsequent runtime polls.

## Queue, Speeds and Automation

Enable managed queueing in Settings and set active download, seed and total
caps. Zero allows no ordinary active torrents in that class. Use Queue position
in the Transfers/context menu to move a selection up/down/top/bottom, then sort
by Queue # to view the order. Manually paused torrents are not resumed by the
queue. Force start bypasses queue caps, not the native VPN/disk guard; Pause
also clears force start. Return to managed queue removes the override.
Paused torrents' retained peer sockets are closed on the next scheduler pass;
this frees the global budget without resuming them. Metadata/hash-fetching
pauses stop the transfer because the library's pause operation ignores those modes.
Downloads are admitted before ordinary seeds when the total cap is scarce;
position order applies within each class. Forced torrents retain precedence.
Queue rank is a library ordering number, not the number of active jobs ahead.

v2.2.3 counts only selected files for completion: skipped
files no longer hold a completed selection in an active download slot. Metadata
jobs are monitored for stalls, and bounded backoff-based discovery refreshes use
the auto-reannounce toggle. Missing tracker/DHT sources appear in the selected
waiting reason. DHT can bootstrap through the VPN adapter; see
[the stall audit](STALL_AUDIT.md).

Settings > Connections and discovery includes **Download connection reserve**:
0-90%, default 25%, with 0 disabling this policy. While eligible downloads or
metadata transfers are active, ordinary seeds share the remaining global peer
budget. Excess seeds wait automatically, even if active-torrent queueing is off.
Over-budget seeds reconnect once when their effective cap changes; stop work is
bounded to eight workers, then new starts are admitted. Caps restore when no
downloads are eligible. User connection overrides are preserved and lower caps
are honored. Forced seeds retain their controls and count against the ordinary
seed budget; forced work can consume download headroom. This is not a hard
per-download reservation or proof that available peers will send data. Review
private-tracker seeding requirements before enabling policies that queue seeds.

Torrent speed limits applies download/upload KiB/s limits to a selection;
0 is unlimited. Global limits still apply. Bandwidth schedules override global
values during matching time windows and fall back to the globals otherwise.

Advanced controls applies connection budgets, upload slots and sequential
downloads to all selected torrents. Blank connection/slot values inherit
Settings defaults. Sequential mode keeps file priorities and disables both
randomized and rarest-first picking; changing it stops/restarts a running
torrent safely. It can reduce swarm efficiency. PEX and encryption policy are
global settings. The upload-slot default is per torrent, not an aggregate cap.
Picker changes during metadata/file verification are refused with a wait
message rather than racing the engine's initialization.

Preview / stream file selects a file from one running torrent. Skipped files
must first have a download priority applied. The native preview dialog supplies
a random, temporary, loopback-only URL for a browser or VLC. Seeking uses the
streaming piece picker; verified data is streamed in bounded buffers, not loaded
as a whole file. Keep the dialog open. Closing it shuts down the local server
and restores the saved normal/sequential policy. One preview per torrent, four
total. Browser codecs vary; MKV commonly needs VLC. This is not a public media
server and does not bypass queue/VPN/disk guards to start a stopped torrent.

The RSS / watch folder page edits feeds, intervals (1-1440 minutes), include/
exclude title regex, category and optional absolute save folder. Auto-download
is disabled by default. Save settings activates changes; Check now checks the
saved configuration. RSS and Atom enclosures are supported. Feed bodies,
metadata downloads and history are bounded; XML external entities are blocked.
HTTP credentials/passkeys in URLs are sensitive: protect your profile and
backups. Do not share private tracker feed URLs in screenshots/log reports.

A watch folder imports stable .torrent files from that folder only, using the
default download folder; it does not move/delete input metadata. Blank disables
watching. RSS/watch-folder additions respect the transfer guard and queue.

## Creation and Migration

File > Create torrent hashes a file/folder on a background worker, with progress
and Cancel. Choose v1 or hybrid v1/v2 metadata, trackers, comment and optional
private mode. Private mode requires a tracker. Existing output files are not
overwritten, output must be outside a source folder, and symbolic links/junctions
are rejected. Add the output .torrent separately when ready to seed.

File > Import from another torrent client accepts a .torrent file, a metadata
folder, or uTorrent/BitTorrent resume.dat with adjacent metadata. qBittorrent
BT_backup .fastresume paths/categories are read when present. Original files
are untouched; duplicates remain unchanged. Imports stay paused. Review paths,
then Verify downloaded files before Resume. Piece bitfields, credentials,
peer history and the other client's queue state are deliberately not trusted
or imported. Back up your current profile before a large migration.

## Windows Controls

File/Edit/Transfers/View/Tools/Help menus expose the native workflows, settings,
backup/restore, diagnostics, logs and update checks. Ctrl+F focuses search.
The taskbar shows aggregate byte-weighted progress for incomplete unpaused
torrents, with thumbnail buttons for Pause all and Resume all.

Minimize stays on the Windows taskbar. Close-to-tray and start-hidden are
configurable in Settings. The tray offers Show, Pause all, Resume all, WebUI,
update check and Exit. File > Exit stops the runtime rather than hiding it.
Native and API starts respect the engine's enforced socket policy and disk
guard. VPN mode always fails closed, including when legacy pause/bind flags are
off. Select your actual tunnel in Settings; an adapter's name alone does not
prove encryption. The app is not an independent OS firewall. See
[NETWORKING.md](NETWORKING.md) for adapter binding, SOCKS5, DNS, IP filtering,
restart requirements and provider-firewall limitations.

## Isolated Testing

Set this process-scoped environment variable before launching a test copy:

```powershell
$env:CONTROLLARR_PROFILE_DIRECTORY = 'C:\ControllarrNativeLab\profile'
.\Controllarr.exe
```

Use a separate state file, save folder and loopback API port in that profile.
The profile isolates settings, credentials, resume state, logs and extracted
WebUI assets. Custom-profile launches do not register the magnet handler or
change Windows startup registration. DPAPI still ties credentials to the
Windows user; this is not a portable credential bypass.

`scripts/test-windows-native.ps1 -Launch` stages the source under LocalAppData,
runs the logic/WPF tests, builds an x64 preview and
launches it with a lab profile on loopback port 18791. It neither replaces an
installed app nor edits its profile. Pass `-RuntimeIdentifier win-arm64` for a
native Windows-on-ARM preview. Close the lab app before republishing. The WPF
test briefly shows a synthetic window with runtime startup disabled.
Builds are staged separately in `app-win-x64` and `app-win-arm64` under the
lab folder. Both are folder-based self-contained builds. Earlier ARM64 single-
file builds had an intermittent API AccessViolation; short folder-build checks
passed but do not establish a root-cause fix or production readiness.

After closing the lab app, run `scripts/test-windows-api.ps1` to exercise
repeated isolated launches, login, browser index, torrent polling and graceful
shutdown. It defaults to five restarts and checks only the lab profile on
loopback port 18791. `-IdleSeconds`, `-RequestsPerCycle` and `-RestartCount`
adjust the test; `-KeepRunning` leaves the last successful run visible.
`-DisableReadyToRun` is a process-local ARM64 diagnostic, not a confirmed fix.

See [NATIVE_DESKTOP_VALIDATION.md](NATIVE_DESKTOP_VALIDATION.md) for VM results
and remaining validation limits. Production profiles were not used for testing.

## Known Limits and Further Validation

The release owner accepted the automated prechecks for publication. Further
end-user validation should cover real add/download/seed, both deletion modes on
disposable fixtures, mixed category selection, file-priority persistence,
restart/resume, tray/taskbar actions, VPN recovery and Sonarr/Radarr requests.
Long-duration throughput/memory validation is distinct from the 10,000-row
synthetic UI test. Preserve historical release notes rather than claiming
these changes shipped in old versions.

Active queues, RSS/watch-folder intake, metadata migration, tracker editing,
speed limits and torrent creation are implemented in v2.2.0. Exhaustive
uTorrent/BitTorrent compatibility is not claimed: super-seeding, proxy UDP
ASSOCIATE, protected-mode IPv6 and full foreign-client verified-piece resume
conversion remain outside this implementation. The native blocklist control
selects a local text file; it is not a remote subscription/download service.
The remote WebUI does not yet expose the new desktop-only creation/migration
and queue-editing workflows. See [INSTALL.md](INSTALL.md) for both CPU builds.
