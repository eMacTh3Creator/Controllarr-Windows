# v2.2.3 - VPN-bound DHT and torrent discovery reliability

## DHT Through Your VPN

- DHT now works with enforced VPN adapter binding and local IP blocklists.
  Its UDP socket uses the same IPv4 source/interface pinning as peers and
  trackers. Missing adapters and restart-required topology changes fail closed;
  there is no ordinary-interface or IPv6 fallback.
- DHT bootstrap DNS also follows that policy. Adapter DNS is tried first;
  Cloudflare/Quad9 numeric resolvers may be tried through the same forced
  tunnel. System DNS is never used in VPN mode. UDP-first DNS supports tunnel
  resolvers that do not accept TCP; truncated answers use bound TCP fallback.
- Automatic tunnel recognition includes PIA, NordVPN/NordLynx, Proton,
  Mullvad, Surfshark, ExpressVPN, CyberGhost, Windscribe, IVPN and generic
  OpenVPN/TAP/WireGuard/Wintun adapters. Multiple active candidates require an
  explicit adapter selection. This recognizes adapter names, not VPN accounts
  or a guarantee of provider encryption/connectivity.
- DHT has bounded queues/lookup concurrency, bootstrap retries, cache restart,
  cancellation, sender validation and bounded peer records. Private torrents
  continue to exclude DHT. SOCKS5 DHT remains disabled because UDP ASSOCIATE is
  not implemented. Local discovery/router mapping stay off in protected modes.
- Native settings and the WebUI explain these controls; the WebUI now exposes
  adapter selection and the DHT switch. API diagnostics report actual DHT
  state/node counts rather than a hard-coded zero.

## Stall and Intake Repairs

- Failed tracker responses, including HTTP `failure reason` replies incorrectly
  labeled OK by the underlying library, advance to the next tracker in a tier; successful
  tracker minimum/update intervals are honored by bounded early rediscovery.
- Metadata jobs now participate in health monitoring. Optional auto-refresh
  starts after one inactive minute and backs off; it cannot start paused,
  queued, removing or safety-blocked work.
- Queue completion counts selected files, not deliberately skipped files.
- Add/import use asynchronous engine registration and lightweight batched
  saves; socket tracking no longer rescans every connection on every connect.
- Re-adding public torrents can merge source-supplied trackers under the
  existing MergeTrackers policy without changing paths, category or pause state.
- Automatic stall recovery no longer abandons a preferred VPN-forwarded port.

## Install and Safety

Full **x64 and experimental ARM64 Setup.exe installers**, portable ZIPs and
SHA-256 checksums are provided. Exit the old app from its tray, back up
`%AppData%\Controllarr`, and choose **Keep existing settings** when upgrading.
Downloaded files are not moved or deleted by installation. Enable VPN-only
traffic, select your actual tunnel, enable DHT, save and restart after adapter
changes. Keep your provider's kill switch enabled and allow private-LAN access
for Sonarr/Radarr separately. Binaries remain unsigned.

Build, local DHT/engine, WPF, API and installer prechecks are recorded in
[the validation report](https://github.com/eMacTh3Creator/Controllarr-Windows/blob/master/docs/NATIVE_DESKTOP_VALIDATION.md).
The VM passed 203 checks including large fixtures; both exact ZIP payloads
passed API checks, and both installers passed keep/import/uninstall checks.
Tests do not certify PIA/Nord/provider packet leakage, physical x64 hardware or
multi-day production load. ARM64's historical intermittent API crash is not
claimed root-caused. Synchronous post-processing/*arr callbacks, metadata-slot
rotation and stricter post-processing completion verification remain follow-up
work; see [the stall audit](https://github.com/eMacTh3Creator/Controllarr-Windows/blob/master/docs/STALL_AUDIT.md).
