# Policy-Controlled MonoTorrent DHT

Imported from MonoTorrent 3.0.2, commit
`e78faebd0aec117146cffccaaea987ab0629eec0` (MIT). Only the DHT source and eight
shared helpers are included; the rest of the engine remains the pinned NuGet
package. Namespaces are changed to `Controllarr.Core.Dht` to avoid replacing or
patching packaged DLLs. Original license headers are preserved. See the root
`THIRD_PARTY_NOTICES.md`.

Local changes:

- A mandatory, injectable asynchronous bootstrap resolver; no system DNS call
  or shared bootstrap cache exists in this component. The production resolver
  goes through `TorrentNetworkPolicy`, including numeric resolver fallbacks
  that are still forced through the VPN adapter.
- `BoundDhtListener` owns all actual UDP I/O. It closes/rebinds on policy
  changes and applies blocklists to both directions. SOCKS5 remains disabled.
- Bootstrap retry, cancellation, single periodic maintenance timer, restart
  generation guards and compatible bounded node-cache decoding.
- Per-hash deduplication and at most 64 concurrent lookups plus 64 announces;
  1024 queued sends, 256 outstanding queries, 256 decoded receive messages and
  128 pending decode callbacks. Excess work is dropped/retried by normal DHT
  and torrent discovery schedules, not unboundedly queued.
- Sender endpoint matching before response decoding, bounded/depth-checked
  datagrams, incoming query dispatch, token validation before peer allocation,
  correct announced/implied port, 1024 peer-store hashes / 64 peers each and
  30-minute peer expiry.
- Shutdown completes queued/pending queries and cancels DNS; transport send
  errors on responses cannot be incorrectly cast to query messages.

Tests use local UDP DHT nodes, not an internet swarm or production profile.
Review upstream changes and these patches together before upgrading MonoTorrent.
This is not a claim of new BEP coverage, IPv6 DHT or a real-provider leak audit.
