# Controllarr for Windows v2.1.18

System-tray improvements.

## Fixes
- **Tray menu now opens while the window is visible.** Previously the right-click menu only worked when the app was minimized to the tray — the WebView2 control captured Win32 focus so the menu couldn't foreground itself. It now opens reliably in either state.
- **"Exit" now always quits.** The menu's Exit (and the Web UI "Shut down app") now run a single, reliable shutdown path (graceful engine/server stop → app exit) with a hard-stop fallback so the process never lingers.
- **Tray menu actions fixed.** "Open Web UI in Browser" and "Check for Updates" were silently bound to nothing; they now work.

## New
- **Live hover tooltip.** Hovering the tray icon shows a qBittorrent-style readout that updates live: download/upload speed, torrent counts (total/downloading/seeding), listen port, and VPN/disk status.

Drop-in replacement of `Controllarr.exe`.
