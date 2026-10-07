param([Parameter(Mandatory=$true)][string]$ExecutableDirectory)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$lab = Join-Path $env:LOCALAPPDATA 'ControllarrNativeLab'
$directory = [IO.Path]::GetFullPath($ExecutableDirectory)
if (!$directory.StartsWith($lab + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use a disposable lab build only.' }
$profile = Join-Path $lab ('network-profile-' + [guid]::NewGuid().ToString('N'))
$downloads = Join-Path $profile 'downloads'
New-Item -ItemType Directory -Force $downloads | Out-Null
@{ settings = @{
    default_save_path = $downloads; web_ui_host = '127.0.0.1'; web_ui_port = 18792
    vpn_enabled = $true; vpn_interface_id = 'missing-network-test-adapter'
    peer_discovery = @{ dht_enabled = $false; lsd_enabled = $false }
    ui_preferences = @{ automatic_update_checks = $false; close_to_tray = $false }
    torrent_network = @{ proxy_username = 'fixture-user'; proxy_password = 'fixture-secret' }
}; categories = @() } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $profile 'state.json') -Encoding UTF8
$previousProfile = $env:CONTROLLARR_PROFILE_DIRECTORY
$env:CONTROLLARR_PROFILE_DIRECTORY = $profile
$base = 'http://127.0.0.1:18792'
$process = $null
function ResponseText($response) {
    if ($response.Content -is [byte[]]) { return [Text.Encoding]::UTF8.GetString($response.Content) }
    return [string]$response.Content
}
function Login {
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $script:session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
            $response = Invoke-WebRequest "$base/api/v2/auth/login" -Method Post -Body @{ username='admin'; password='adminadmin' } -WebSession $script:session -UseBasicParsing -TimeoutSec 2
            if ((ResponseText $response).Trim() -eq 'Ok.') { return }
        } catch { Start-Sleep -Milliseconds 250 }
        if ($process.HasExited) { throw 'Network fixture app exited during startup.' }
    }
    throw 'Network fixture API did not start.'
}
function GetJson([string]$path) { ResponseText (Invoke-WebRequest "$base$path" -WebSession $script:session -UseBasicParsing -TimeoutSec 5) | ConvertFrom-Json }
function TorrentRows { foreach ($row in (GetJson '/api/v2/torrents/info')) { Write-Output $row } }
function StopFixture {
    Invoke-WebRequest "$base/api/controllarr/shutdown" -Method Post -WebSession $script:session -UseBasicParsing | Out-Null
    if (!$process.WaitForExit(30000)) { throw 'Fixture did not shut down gracefully.' }
}
try {
    $process = Start-Process (Join-Path $directory 'Controllarr.exe') -PassThru
    Login
    $network = GetJson '/api/controllarr/network'
    if ($network.allowed -or !$network.vpn_required) { throw 'Missing VPN did not fail closed.' }
    Invoke-WebRequest "$base/" -WebSession $session -UseBasicParsing | Out-Null
    Write-Output 'PASS LAN API/WebUI remain responsive while torrent networking is blocked'
    $hash = '0123456789012345678901234567890123456789'
    Invoke-WebRequest "$base/api/v2/torrents/add" -Method Post -Body @{ urls="magnet:?xt=urn:btih:$hash" } -WebSession $session -UseBasicParsing | Out-Null
    Invoke-WebRequest "$base/api/v2/torrents/resume" -Method Post -Body @{ hashes=$hash } -WebSession $session -UseBasicParsing | Out-Null
    Invoke-WebRequest "$base/api/v2/torrents/setForceStart" -Method Post -Body @{ hashes=$hash; value='true' } -WebSession $session -UseBasicParsing | Out-Null
    Start-Sleep -Seconds 3
    $rows = @(TorrentRows)
    if ($rows.Count -ne 1 -or $rows[0].state -notlike 'queued*') { throw 'API add/resume bypassed the torrent guard.' }
    Write-Output 'PASS qBittorrent add/resume/force-start cannot bypass the VPN guard'
    $controls = @{ MaximumConnections=12; UploadSlots=3; Sequential=$true } | ConvertTo-Json
    Invoke-WebRequest "$base/api/controllarr/torrents/$hash/options" -Method Post -Body $controls -ContentType 'application/json' -WebSession $session -UseBasicParsing | Out-Null
    $saved = GetJson "/api/controllarr/torrents/$hash/options"
    if (!$saved.Sequential -or $saved.MaximumConnections -ne 12) { throw 'Advanced options did not save.' }
    $settings = GetJson '/api/controllarr/settings'
    if ($settings.torrent_network.ProxyPassword -ne 'fixture-secret') { throw 'DPAPI secret did not load.' }
    $settings.vpn_enabled = $false
    Invoke-WebRequest "$base/api/controllarr/settings" -Method Post -Body ($settings | ConvertTo-Json -Depth 30) -ContentType 'application/json' -WebSession $session -UseBasicParsing | Out-Null
    $network = GetJson '/api/controllarr/network'
    if ($network.allowed -or !$network.restart_required) { throw 'Topology change reopened old torrent routes.' }
    Write-Output 'PASS network topology changes latch the session closed until restart'
    StopFixture
    if ((Get-Content (Join-Path $profile 'state.json') -Raw).Contains('fixture-secret')) { throw 'Proxy password leaked into state.json.' }
    Write-Output 'PASS SOCKS5 password is absent from plaintext state.json'
    $process = Start-Process (Join-Path $directory 'Controllarr.exe') -PassThru
    Login
    $network = GetJson '/api/controllarr/network'
    if (!$network.allowed -or $network.restart_required) { throw 'New networking settings did not take effect after restart.' }
    $restored = GetJson "/api/controllarr/torrents/$hash/options"
    if (!$restored.Sequential -or $restored.UploadSlots -ne 3) { throw 'Advanced controls did not survive restart.' }
    if ((GetJson '/api/controllarr/settings').torrent_network.ProxyPassword -ne 'fixture-secret') { throw 'Encrypted proxy secret did not survive restart.' }
    Write-Output 'PASS advanced controls and encrypted proxy secret survive process restart'
    $otherHash = 'b' * 40
    Invoke-WebRequest "$base/api/v2/torrents/add" -Method Post -Body @{ urls="magnet:?xt=urn:btih:$otherHash" } -WebSession $session -UseBasicParsing | Out-Null
    Invoke-WebRequest "$base/api/v2/torrents/delete" -Method Post -Body @{ hashes="$hash|$($hash.ToUpperInvariant())"; deleteFiles='false' } -WebSession $session -UseBasicParsing | Out-Null
    $remaining = @(TorrentRows)
    if ($remaining.Count -ne 1 -or $remaining[0].hash -ne $otherHash) { throw 'Batch API deletion did not preserve the unselected torrent' }
    Invoke-WebRequest "$base/api/v2/torrents/delete" -Method Post -Body @{ hashes='all'; deleteFiles='true' } -WebSession $session -UseBasicParsing | Out-Null
    if (@(TorrentRows).Count -ne 0) { throw 'API hashes=all deletion did not remove the disposable library' }
    Write-Output 'PASS API deletion deduplicates selection, preserves unselected torrents and supports hashes=all'
    StopFixture
    $process = Start-Process (Join-Path $directory 'Controllarr.exe') -PassThru
    Login
    if (@(TorrentRows).Count -ne 0) { throw 'API-removed fixtures returned after restart' }
    StopFixture
    Write-Output 'PASS API batch removals persist across restart'
    Write-Output "Network/API tests passed with isolated profile $profile"
} finally {
    if ($process -and !$process.HasExited) {
        try { StopFixture } catch { Write-Warning 'Fixture remains running; shut it down before repeating this test.' }
    }
    $env:CONTROLLARR_PROFILE_DIRECTORY = $previousProfile
}
