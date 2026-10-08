# v2.3.0 - Sonarr Import Repair and Native Mobile Remote Support

- Correct qBittorrent `save_path`/`content_path` reporting for owned multi-file folders, resolving Sonarr's base-download-directory import warning without moving properly isolated downloads.
- Honor `contentLayout=Subfolder` and `NoSubfolder` for new adds; `Original` follows category/global policy. Trailing backslashes are not required.
- Add confirmed **Repair import folder layout...** to native context/Transfers menus. Move selected flat payloads into owned folders, preserve pause state, reject collisions and leave proper folders alone.
- Add authenticated Remote Protocol 1: bounded 100-row default/500-row maximum pages, server-side category/search, 512-event epoch/cursor journal, recheck/reannounce/move/repair endpoints.
- Optional LAN multicast discovery for the native iOS manager; disable in native/browser Settings, restart to apply. Loopback-only servers are not advertised; no credentials are broadcast.
- Retain native WPF performance, queue/peer-budget diagnosis, pause-first bulk removal, bound DHT/VPN network policy and profile-preserving x64/ARM64 installers/portable ZIPs.

The native iOS source/simulator preview is published in the [Mac repository](https://github.com/eMacTh3Creator/Controllarr/tree/main/iOS).
It supports torrent/category/settings controls, files and diagnostics, multiple
servers, hostnames and notification options. It is not a public installable
iPhone/TestFlight release. Background alerts are best-effort refresh, not APNs
push. See [mobile setup](docs/MOBILE.md).

## Safety and Scope

Exit Controllarr from the tray before installation. Keep existing settings by
default, or import an older profile with a metadata backup. Installation does
not move/delete downloaded files. Folder repair is explicitly confirmed.
Windows builds remain unsigned. ARM64 remains experimental; VM/CI prechecks do
not establish provider-specific leak freedom, long-run Plexbox stability, or
the cause of a historical single-file ARM64 crash. Built-in automatic update
installation and full advanced-client Mac parity remain unfinished.

## Verification

248 final VM logic/real-engine checks passed, including 1,000 paused local torrents and
verified loopback metadata/payload transfers. Native UI/selection and profile-import
checks passed. Both final architecture packages passed isolated API/network and
installer install/upgrade/import/uninstall checks; SHA-256 values were verified.
x64 ran under ARM emulation. These are not a long-duration production soak or a
1,000-active-WAN-transfer benchmark. See [validation](docs/NATIVE_DESKTOP_VALIDATION.md).
