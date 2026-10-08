# Native iOS Remote Preview

Controllarr v2.3.0 supports the native iPhone/iPad remote manager in the
[Mac repository's iOS project](https://github.com/eMacTh3Creator/Controllarr/tree/main/iOS).
Requires iOS 17+. Source/simulator packages are developer previews, not public
IPAs or TestFlight releases.

Change default credentials, set WebUI host to `0.0.0.0` or a LAN IP and enable
**Advertise to iOS on the LAN**, then restart. Allow private-network firewall
access. Discovery uses UDP 5353 multicast `_controllarr._tcp`; it may not cross
VLANs or VPN firewalls. Manual hostnames/IPs remain available. Loopback-only
servers are not advertised, and no credentials are advertised.

Connect from **Instances** using `plexbox.local:8791`, a private IP or an HTTPS
reverse-proxy hostname/base path. HTTP requires explicit opt-in for a trusted
LAN/private VPN. Do not expose plaintext port 8791 to the Internet. iOS stores
passwords in its private Keychain without biometric/password access-control
prompts. Windows credential/profile handling is unchanged.

Native controls include 100-row searchable/category pages, multiple instances,
bulk pause/resume/delete/category, magnets/files, force/recheck/reannounce/move,
folder repair, category CRUD, schema-preserving server settings, file priorities,
trackers/peers, health clearing, post retries and diagnostics.

Completion/error/VPN-disconnect/port-change alerts use foreground polling and
best-effort iOS background refresh for the selected server. Background delivery
is not guaranteed; force-quitting can stop it. The 512-event in-memory journal
is not an audit log. Reliable APNs push and Apple distribution remain unfinished.

Authenticated `/api/controllarr/remote`, `/remote/torrents` and `/remote/events`
routes use existing SID login. Windows schemas stay snake_case, declared by
capabilities. See the [full protocol](https://github.com/eMacTh3Creator/Controllarr/blob/main/docs/REMOTE_API.md)
and [platform coverage](https://github.com/eMacTh3Creator/Controllarr/blob/main/docs/PARITY.md).
