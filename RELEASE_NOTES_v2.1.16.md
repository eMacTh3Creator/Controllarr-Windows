# Controllarr for Windows v2.1.16

Bug-fix release for the **preferred forwarded port**.

## Fixes
- **Preferred port now saves correctly.** The Web UI was omitting `preferred_listen_port` from the settings it sent on save, so the value was dropped — the checkbox would untick and the port was never stored. The Web UI now includes it (and preserves any settings the form doesn't render, instead of resetting them on save).
- **Preferred port now takes effect.** Saving a preferred port now re-binds the torrent engine to it immediately (with a tracker reannounce) instead of requiring a restart.
- **Preferred port wins at startup.** A user-set preferred forwarded port now takes precedence over the last cycled port on relaunch, so it is honored consistently. PortWatcher still falls back to the configured range if the port goes stale.

No other changes. Upgrading is a drop-in replacement of `Controllarr.exe`.
