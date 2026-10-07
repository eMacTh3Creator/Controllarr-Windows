# Install Controllarr for Windows

This guide covers v2.2.0. Both packages contain the same native WPF desktop;
ARM64 remains experimental. Releases before v2.2.0 used a different EXE format.

| Package | Use On |
|---------|--------|
| [Controllarr-2.2.0-win-x64.zip](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.0/Controllarr-2.2.0-win-x64.zip) | 64-bit Intel/AMD Windows 10/11 PCs, including an x64 Plexbox |
| [Controllarr-2.2.0-win-arm64.zip](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.0/Controllarr-2.2.0-win-arm64.zip) | Windows 11 on ARM devices; experimental |

Check Settings > System > About > System type if unsure. ARM64 is not the
correct build for an Intel/AMD PC. These are not macOS applications.

1. Back up the Controllarr profile and stop any existing Controllarr process.
2. Extract the entire chosen ZIP into a new permanent app folder. Do not run
   inside the ZIP or copy only the EXE; its runtime DLLs must remain alongside it.
3. Run `Controllarr.exe`. No separate .NET, Edge or WebView2 install is needed.
4. Use the native window for local control. Optional browser/API access defaults
   to `http://127.0.0.1:8791`, with `admin` / `adminadmin`; change these before LAN use.

Settings, downloaded metadata, DPAPI credentials and logs are stored under
`%AppData%\Controllarr`, separately from the executable folder. Do not launch
two architecture copies against the same profile at once. Switching architecture
does not intentionally reset the profile; keep a backup before any upgrade.
Custom isolated profiles are described in [DESKTOP.md](DESKTOP.md).

Verify the ZIP against the release's [SHA256SUMS.txt](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.0/SHA256SUMS.txt) with `Get-FileHash .\Controllarr-2.2.0-win-x64.zip -Algorithm SHA256` (substitute the ARM64 filename as needed). Never update only the EXE: stop the app and replace the entire application folder, keeping your separate profile and a backup.

The packages are unsigned. Obtain them only from the project or your own build,
verify the published SHA-256 checksum when provided, and do not disable Defender,
SmartScreen or the Windows firewall globally. ZIP extraction does not imply a
trusted publisher. A checksum detects corruption; it is not a code signature.

Earlier ARM64 single-file builds reproduced an API AccessViolation. This
release uses a complete app folder instead; subsequent short checks passed,
but the root cause and long-duration stability remain unproven. Use an isolated
profile before migrating a production library. x64 testing in an ARM VM uses
Windows emulation and does not replace Intel/AMD hardware validation.

Enable **Enforce VPN-only torrent traffic** and select your
actual VPN adapter in Settings. Save, quit and reopen the app. Torrent sockets
are pinned to that adapter; the LAN WebUI/API listener is separate. Configure
the WebUI bind address and provider's allow-LAN/firewall rules independently.
Keep the provider's kill switch on. Read [NETWORKING.md](NETWORKING.md) before
production use: protected modes have deliberate discovery/IPv6 restrictions,
and real-provider packet-capture/drop/reconnect testing is still required.

## Build Both Packages

On Windows with a stable .NET SDK:

```powershell
.\scripts\package-windows.ps1
```

The script validates the solution, runs desktop logic and WPF tests, and writes
both self-contained ZIPs and `SHA256SUMS.txt` under `publish\packages`.
It does not tag, upload or publish a GitHub release.
