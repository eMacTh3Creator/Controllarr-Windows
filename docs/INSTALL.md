# Install Controllarr for Windows

v2.2.3 provides a full per-user installer and optional portable ZIP for each CPU.
Both contain the same native WPF desktop. No separate .NET, Edge or WebView2
installation is needed. ARM64 remains experimental.

| Installer | Use On |
|-----------|--------|
| [Download x64 Setup.exe](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.3/Controllarr-2.2.3-win-x64-Setup.exe) | Intel/AMD 64-bit Windows 10/11, including an x64 Plexbox |
| [Download ARM64 Setup.exe](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.3/Controllarr-2.2.3-win-arm64-Setup.exe) | Windows 11 on ARM; experimental |

Check Windows Settings > System > About > System type if unsure. ARM64 is not
the correct build for an Intel/AMD PC. These are not macOS applications.

## Install or Upgrade

1. Back up `%AppData%\Controllarr`. Finish or cancel any removal batch, then
   choose **Exit** from the old app's tray menu. Closing the window may leave it
   running. Do not force-kill during deletion or state saves.
2. Run the installer matching your CPU. It installs the complete app under
   `%LocalAppData%\Programs\Controllarr` by default without administrator access,
   with a Start menu entry, optional desktop shortcut, and Windows uninstaller.
3. On **Settings and torrent library**, choose **Keep existing settings** for a
   normal upgrade, including transition from a portable build. Your current
   AppData profile is automatically reused, not reset or re-imported.
4. For a backup or custom profile, select **Import a previous Controllarr Windows
   profile folder** and browse to the folder containing `state.json`, not the
   executable or torrent download folder. Exit any app using that source first.
5. Launch Controllarr. Fresh-profile browser/API access defaults to
   `http://127.0.0.1:8791`, with `admin` / `adminadmin`; change these before LAN use.

Later installers upgrade the same per-user installation. This is a packaging
foundation for a future built-in updater; automated Windows download/installation
is not implemented in this release.

## VPN and DHT After Upgrading

Enable **Settings > VPN Protection > Enforce VPN-only torrent traffic** and
choose your actual tunnel adapter. PIA, NordVPN/NordLynx and popular generic
tunnels are recognized automatically; multiple active candidates require an
explicit selection. Do not choose Ethernet/Wi-Fi. Enable DHT in Connections and
discovery, save and restart after adapter changes. Existing DHT-off settings
are preserved; installation never silently enables it.

DHT UDP and bootstrap DNS use the forced VPN adapter. Keep the provider's kill
switch enabled. SOCKS5 disables DHT/UDP; protected IPv6 remains off. For remote
Sonarr/Radarr, separately allow private-LAN management through your provider
and Windows firewall. Do not disable the firewall or expose the management
port publicly. See [NETWORKING.md](NETWORKING.md) for diagnostics and limits.

## Profile Import

Import copies settings/categories (`state.json`), torrent metadata/resume data
(`resume`), desktop layout and RSS history. Cached metadata paths are rebased to
the new profile; payload paths stay unchanged. Downloaded files are never copied,
moved or deleted by installation. Payloads must still be present at their recorded
paths for torrents to resume normally.

Saved DPAPI passwords can be imported only with the Windows account/DPAPI keys
that encrypted them, normally on the same PC. Uncheck **Import saved passwords**
when transferring from another account/PC. Enter passwords again in Settings;
the imported WebUI is restricted to loopback until you explicitly restore LAN
access, avoiding fallback credentials exposed on the network.

Import validates first, rejects links/junctions, and limits metadata to 100,000
files / 2 GiB (32 MiB per JSON file). Existing target metadata is backed up under
`%AppData%\Controllarr-profile-backups\<timestamp-id>` before replacement, with
rollback on handled copy/move errors. Source files are untouched. Keep a separate
backup for power loss/storage failure. Backups can contain private paths and API
keys; protect them like your profile.

Uninstall removes app files, not the AppData profile or downloaded files. Do not
run two architectures against the same profile. Custom profiles use
`CONTROLLARR_PROFILE_DIRECTORY`; see [DESKTOP.md](DESKTOP.md).

## Portable Alternative

[x64 ZIP](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.3/Controllarr-2.2.3-win-x64.zip)
and [ARM64 ZIP](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.3/Controllarr-2.2.3-win-arm64.zip)
remain available. Stop the old app, extract every file into a new permanent folder,
and run `Controllarr.exe` there. Keep its DLLs together; do not run inside the ZIP
or update only the EXE. Settings remain in AppData.

## Security and Validation Limits

Installers and app binaries are unsigned; SmartScreen may warn of an unknown
publisher. Download only from the project or your own build and verify
[SHA256SUMS.txt](https://github.com/eMacTh3Creator/Controllarr-Windows/releases/download/v2.2.3/SHA256SUMS.txt):

```powershell
Get-FileHash .\Controllarr-2.2.3-win-x64-Setup.exe -Algorithm SHA256
```

A checksum detects corruption; it is not a code signature. Do not disable
Defender, SmartScreen or the firewall globally. The installer uses a process-local
PowerShell execution-policy override for its bundled helper, not a persistent
policy change.

Earlier ARM64 single-file builds reproduced an API AccessViolation. Folder builds
passed short checks, but the root cause and long-term stability remain unproven.
Try an isolated profile before migrating production. x64 tests on an ARM VM use
emulation and do not replace physical Intel/AMD hardware tests.

For VPN use, select **Enforce VPN-only torrent traffic** and the actual adapter,
save, exit and reopen. The LAN WebUI/API listener is independent; provider
allow-LAN/firewall rules still matter. Keep the provider's kill switch on and read
[NETWORKING.md](NETWORKING.md) before production use.

## Build Packages

On Windows with a stable .NET SDK and Inno Setup 6 (6.7.3 tested):

```powershell
.\scripts\package-windows.ps1 -CompilerPath 'C:\Path\To\Inno Setup 6\ISCC.exe'
```

The script runs solution, engine and WPF prechecks, then creates both CPU ZIPs,
both Setup EXEs and checksums under `publish\packages`. It does not tag or upload.
