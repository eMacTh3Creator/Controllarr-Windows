# Torrent Networking and Advanced Controls

Applies to Windows v2.2.0-v2.2.2, not historical v2.1.19 builds.

v2.2.2 adds bounded download peer headroom (25% by default, 0 disables it) and
global/per-torrent cap diagnostics. This changes scheduling, not routing: no
connection-budget or queue action bypasses VPN/proxy/disk guards. A full global
peer budget is distinct from a closed VPN adapter or missing swarm peers.
Windows uses MonoTorrent 3.0.2; do not assume macOS/libtorrent behavior.

## VPN Binding

Enable **Settings > VPN Protection > Enforce VPN-only torrent traffic**. Select
the actual VPN tunnel, not Ethernet/Wi-Fi; automatic detection uses TAP,
WireGuard/Wintun descriptions or the configured name prefix. Explicit adapter
selection uses its stable Windows interface ID and never falls back to another
adapter. Selecting a physical adapter does not turn it into an encrypted VPN.

Save, quit and reopen Controllarr. Network topology changes latch torrent
networking closed until restart: this avoids leaving old unbound listeners,
discovery engines or pooled connections alive with new settings. Changes to
VPN selection, proxy server/credentials and blocklist file path require restart.
Editing a blocklist file at the same path also requires restart to reload it.

Peer TCP, HTTP/HTTPS trackers, webseeds, UDP trackers and tunnel DNS use
policy-owned sockets. VPN sockets bind the tunnel's IPv4 source address and
set Windows `IP_UNICAST_IF` to its interface index. Address/network changes
close existing sockets; polling refreshes status and queue recovery. Missing
or invalid adapters block socket creation and all torrent starts, including
API resumes, force start and queue starts. User-paused torrents stay paused.
Legacy `vpn_bind_interface` / `vpn_kill_switch` booleans cannot weaken enabled
VPN enforcement in this Windows release.

Protected IPv6 torrent connections are deliberately rejected, not routed on
the ordinary interface. Hostname resolution in VPN mode queries the adapter's
IPv4 DNS servers over bound TCP port 53. No system-DNS fallback occurs. A VPN
with no usable DNS server, no TCP DNS support, or an unreachable resolver may
allow numeric-IP connections while hostname trackers/webseeds fail closed.

DHT, local peer discovery and UPnP/NAT-PMP mapping are disabled when VPN binding,
SOCKS5 or a nonempty IP blocklist is active. Their built-in transports have
independent discovery/DNS paths. Use tracker-backed torrents, PEX and your
provider's forwarded incoming port. Trackerless magnets may not find peers.

Microsoft documents the interface option and its byte-order requirements in
[IPPROTO_IP socket options](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options).

## LAN WebUI and API

The Kestrel WebUI/API listener is not bound by the torrent socket policy. For
Sonarr/Radarr on `192.168.1.5` and Controllarr on `192.168.1.122`, bind WebUI to
`192.168.1.122` (or `0.0.0.0`), restart, then use
`http://192.168.1.122:8791`. Change default credentials and restrict allowed LAN
clients before exposing it. Do not forward this HTTP management port publicly.

The app cannot bypass a VPN provider's inbound LAN firewall or Windows firewall
rules. Enable the provider's local-network allowance and an appropriately
scoped private-network inbound rule. Keep the provider's kill switch enabled
as defense in depth. This is app-level socket enforcement, not an OS-wide
firewall guarantee. RSS feed retrieval, *arr callbacks and update checks use
their normal HTTP routing, not the torrent transport policy; passkey feed URLs
are sensitive. A file streaming preview is deliberately loopback-only.

## SOCKS5 and Encryption

Specify a numeric IPv4 SOCKS5 server and port (1-65535). Optional username and
password use RFC1929 authentication; leave username blank for no authentication.
The password is stored with current-user DPAPI alongside the WebUI password,
not as plaintext in `state.json`. DPAPI does not request per-access permissions
or a login password. Re-enter credentials after moving to another Windows
user/profile. Secrets-included backups contain plaintext secrets; protect them.

TCP peers, trackers and webseeds connect through SOCKS5; tracker/webseed hostname
resolution is remote, with no local DNS or direct-connection fallback. VPN
binding and SOCKS5 can be combined: the proxy socket itself uses the tunnel.
SOCKS5 mode disables incoming peers, UDP trackers and discovery; UDP ASSOCIATE
is not implemented. SOCKS5/RFC1929 does not itself encrypt traffic or passwords.
Use VPN binding across untrusted networks. Authentication failure is fail-closed.

Encryption **Prefer** allows encrypted peers with plaintext fallback;
**Require** permits RC4-full protocol encryption only; **Disable** permits
plaintext peers only. Changes disconnect existing policy-owned connections so
the new policy applies to their replacements. BitTorrent protocol encryption is
not strong modern transport security and does not hide tracker/DNS traffic.

## IP Blocklist

Choose a local UTF-8 text file, up to 5 MiB and 100000 entries. Each nonempty
line is an IPv4/IPv6 address or CIDR; `#` begins a comment. Invalid records reject
configuration instead of silently disabling filtering. Ranges are merged at
startup and checked with binary search; no per-peer file parsing is performed.

Filtering covers incoming/outgoing peers and resolved tracker/webseed IPs.
The list does not filter LAN management clients; use the WebUI's separate
allowlist for those. Proxy-resolved hostname destinations cannot be checked
locally, so combining SOCKS5 with a nonempty blocklist rejects hostname tracker/
webseed destinations. Numeric destinations remain filterable. There is no
automatic blocklist download/subscription service or PeerGuardian format import.

## Diagnostics and Limits

Authenticated `GET /api/controllarr/network` reports whether torrent networking
is allowed, its bound adapter/address, proxy use and restart requirement.
`GET/POST /api/controllarr/torrents/{hash}/options` reads/writes connection and
upload-slot overrides and sequential mode. Native Transfers/context menus
apply these controls to a multi-selection. Blank values inherit defaults.
The legacy `global_max_upload_slots` field supplies a per-torrent default;
there is no aggregate upload-slot limiter in this engine. The global peer
connection cap is still enforced independently.

Streaming previews prioritize pieces needed for seeks and serve verified
data through a bounded, temporary local endpoint. Closing the dialog restores
the saved picker and stops the listener. Picker changes are refused during
initial verification; wait until the torrent is downloading/seeding. MonoTorrent
3.0.2 reports `SupportsInitialSeed = false`, so super-seeding has no placeholder
switch; the corresponding qBittorrent endpoint returns 501.

Engine settings updates are serialized so a bandwidth/port change cannot
overwrite encryption settings from a stale snapshot. Resume checkpoints use
atomic replacements under one writer instead of overlapping library startup
writes. They are saved at most every 30 seconds by the normal poll loop, on
engine-state saves and after graceful shutdown. Checkpoint writes still use
best-effort progress snapshots: an unclean exit or externally changed files
may require rechecking. Explicit verification invalidates the old checkpoint.

Automated socket/API tests are not a real-provider leak certification. Before
production migration, capture traffic during adapter drops, reconnects, DNS
failure and forwarded-port changes; verify LAN *arr access and run a multi-day
mixed-library soak. See [NATIVE_DESKTOP_VALIDATION.md](NATIVE_DESKTOP_VALIDATION.md).
