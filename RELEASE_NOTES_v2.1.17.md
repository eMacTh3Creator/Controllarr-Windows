# Controllarr for Windows v2.1.17

## New
- **Shut down from the Web UI.** A **Shut down app** button now sits next to **Sign out** in the sidebar. It cleanly stops the engine and embedded server and closes the app (handy when running the WebView2 window or controlling Controllarr from a browser). A new `POST /api/controllarr/shutdown` endpoint backs it, and the UI shows a "Shutting down…" screen.

Drop-in replacement of `Controllarr.exe`.
