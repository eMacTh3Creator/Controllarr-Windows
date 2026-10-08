# v2.2.4 - Clean torrent folders and safe storage moves

## Storage Layout

- Add **Create a subfolder for each new torrent** to native and browser category editors, plus an uncategorized default in Settings.
- New categories default to organized folders. Existing profiles keep their previous policy until you enable the checkbox and save.
- Single-file and multi-file intake share a name + short hash folder without double nesting. Nameless magnets use a stable hash-based name before and after metadata arrives.
- Trailing backslashes are optional and do not create subfolders. Explicit save paths still override the category base.
- Completed/manual moves preserve each torrent's outer folder, reject destination-file collisions and save new paths for restart. Repeated moves to the same parent are idempotent.
- Native category changes now honor Always/Ask/Never move policy; API category changes update the live engine as well as persisted labels.
- Category hash lookup remains case-insensitive through state saves/restarts, repairing mixed-case API labels and legacy duplicate-case entries.
- Post-processing requires metadata and 100% of selected files. Extraction reads only the torrent's selected file inventory, not neighboring archives in a shared category root. Failed moves appear as failures rather than successful completion.
- qBittorrent torrent information/properties report the actual `content_path` for remote clients.

## Upgrade and Downloads

Exit the old app, run the matching **x64** or **ARM64** installer, and keep the
existing profile. Optional profile import/backup and portable ZIPs remain
available. No separate .NET or WebView2 is needed. Verify the included
`SHA256SUMS.txt`; Windows binaries are unsigned.

**For an existing category such as tv-sonarr:** enable the new checkbox and
click Save Category. This affects future intake, not files already downloaded
or registered. Old loose files are not silently moved. Review
[the storage guide](https://github.com/eMacTh3Creator/Controllarr-Windows/blob/master/docs/STORAGE.md)
for examples, completed moves and Sonarr/Radarr Remote Path Mappings.

## Validation and Limits

Release prechecks use disposable profiles in the Windows 11 ARM64 VM, including
real payload verification, storage moves and restarts. x64 packages are checked
under Windows-on-ARM emulation. The native WPF harness covers 10,000-row
virtualization and checkbox bindings; WebUI/settings and website download
checks run separately. Exact-package install/API results are recorded in
[the validation report](https://github.com/eMacTh3Creator/Controllarr-Windows/blob/master/docs/NATIVE_DESKTOP_VALIDATION.md).

ARM64 remains experimental after an earlier single-file API crash whose root
cause is not established. Physical x64 hardware, real-provider leak testing,
production network shares and multi-day load remain unverified. A multi-file
move is not a transactional filesystem operation; disk errors can leave some
files at each location. This release retains the v2.2.3 VPN-bound DHT/discovery
repairs and does not add automatic updater installation.
