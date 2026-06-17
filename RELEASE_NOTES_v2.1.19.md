# Controllarr for Windows v2.1.19

## Critical fix — torrents now persist across restarts and updates
Previously the torrent **list** was never saved: MonoTorrent's fast-resume only restored *progress* for torrents that were re-added, and nothing re-added them on launch — so every restart (and every version update) started with an empty client.

Now the full engine state (every torrent, its save path and state) is saved to `%AppData%\Controllarr\resume\engine.state` on each add/remove and on shutdown, and restored — then resumed — on startup. Because it lives in your user profile, it carries over when you download a new version.

### One-time note for existing users
Torrents added in an older build were never persisted, so they can't be recovered automatically — **re-add your current torrents once** after updating to v2.1.19. From then on they (and their progress) survive restarts and updates.

Drop-in replacement of `Controllarr.exe`.
