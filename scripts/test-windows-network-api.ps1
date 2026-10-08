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
    peer_discovery = @{ dht_enabled = $true; lsd_enabled = $false }
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
    $capabilities = GetJson '/api/controllarr/remote'
    if ($capabilities.protocol -ne 1 -or $capabilities.platform -ne 'Windows' -or $capabilities.settingsStyle -ne 'snake_case') { throw 'Remote capabilities are incompatible.' }
    $events = GetJson '/api/controllarr/remote/events'
    if (!$events.reset -or !$events.epoch) { throw 'Event subscription did not establish a baseline.' }
    $next = GetJson ("/api/controllarr/remote/events?epoch=$($events.epoch)&cursor=$($events.cursor)")
    if ($next.reset) { throw 'Valid event cursor unexpectedly reset.' }
    Write-Output 'PASS authenticated Remote Protocol 1 capabilities and bounded event subscription'
    $network = GetJson '/api/controllarr/network'
    if ($network.allowed -or !$network.vpn_required) { throw 'Missing VPN did not fail closed.' }
    if (!$network.dht_allowed -or $network.dht_state -eq 'Disabled' -or $network.dht_nodes -ne 0 -or @($network.adapters).Count -lt 1) {
        throw 'Bound DHT/adapter diagnostics are missing or unsafe with an unavailable VPN.'
    }
    if ((GetJson '/api/controllarr/stats').dht_nodes -ne 0 -or (GetJson '/api/v2/transfer/info').dht_nodes -ne 0) { throw 'DHT stats disagree with the blocked fixture.' }
    Write-Output 'PASS enabled DHT stays fail-closed with missing VPN and exposes runtime/adapter diagnostics'
    Invoke-WebRequest "$base/" -WebSession $session -UseBasicParsing | Out-Null
    Write-Output 'PASS LAN API/WebUI remain responsive while torrent networking is blocked'
    $categoryPath = Join-Path $downloads 'category'
    $category = @{ name='storage-fixture'; save_path=$categoryPath; complete_path=(Join-Path $downloads 'completed'); create_torrent_subfolder=$true }
    Invoke-WebRequest "$base/api/controllarr/categories" -Method Post -Body ($category | ConvertTo-Json) -ContentType 'application/json' -WebSession $session -UseBasicParsing | Out-Null
    $savedCategory = $null
    foreach ($item in (GetJson '/api/controllarr/categories')) {
        if ($item.name -eq 'storage-fixture') { $savedCategory = $item }
    }
    if (!$savedCategory.create_torrent_subfolder) { throw 'Category subfolder option did not save.' }
    $hash = '0123456789012345678901234567890123456789'
    Invoke-WebRequest "$base/api/v2/torrents/add" -Method Post -Body @{ urls="magnet:?xt=urn:btih:$hash&dn=Storage%20Fixture"; category='storage-fixture' } -WebSession $session -UseBasicParsing | Out-Null
    Invoke-WebRequest "$base/api/v2/torrents/resume" -Method Post -Body @{ hashes=$hash } -WebSession $session -UseBasicParsing | Out-Null
    Invoke-WebRequest "$base/api/v2/torrents/setForceStart" -Method Post -Body @{ hashes=$hash; value='true' } -WebSession $session -UseBasicParsing | Out-Null
    Start-Sleep -Seconds 3
    $rows = @(TorrentRows)
    $page = GetJson '/api/controllarr/remote/torrents?category=storage-fixture&limit=1'
    if ($page.total -ne 1 -or @($page.items).Count -ne 1 -or $page.items[0].hash -ne $hash) { throw 'Remote category paging disagrees with the engine.' }
    Write-Output 'PASS remote torrent page filters by category without returning the full library'
    if ($rows.Count -ne 1 -or $rows[0].state -notlike 'queued*') { throw 'API add/resume bypassed the torrent guard.' }
    $expectedPath = Join-Path $categoryPath 'Storage Fixture [012345678901]'
    if ($rows[0].save_path -ne $expectedPath -or $rows[0].content_path -ne $expectedPath) { throw 'API intake did not use the category subfolder without a trailing slash.' }
    Write-Output 'PASS category subfolder settings route API magnets into their own folder before metadata'
    Write-Output 'PASS qBittorrent add/resume/force-start cannot bypass the VPN guard'
    if ($rows[0].priority -ne 1 -or $rows[0].status_reason -notlike '*VPN*') { throw 'Queue rank or VPN waiting diagnosis missing from API' }
    $controls = @{ MaximumConnections=12; UploadSlots=3; Sequential=$true } | ConvertTo-Json
    Invoke-WebRequest "$base/api/controllarr/torrents/$hash/options" -Method Post -Body $controls -ContentType 'application/json' -WebSession $session -UseBasicParsing | Out-Null
    $saved = GetJson "/api/controllarr/torrents/$hash/options"
    if (!$saved.Sequential -or $saved.MaximumConnections -ne 12) { throw 'Advanced options did not save.' }
    $settings = GetJson '/api/controllarr/settings'
    if ($settings.torrent_network.ProxyPassword -ne 'fixture-secret') { throw 'DPAPI secret did not load.' }
    $settings.vpn_enabled = $false
    $settings.create_torrent_subfolders = $true
    $settings.peer_discovery.dht_enabled = $false
    $settings.connection_limits.global_max_connections = 75
    $settings.connection_limits.download_reserve_percent = 50
    Invoke-WebRequest "$base/api/controllarr/settings" -Method Post -Body ($settings | ConvertTo-Json -Depth 30) -ContentType 'application/json' -WebSession $session -UseBasicParsing | Out-Null
    $network = GetJson '/api/controllarr/network'
    if ($network.allowed -or !$network.restart_required) { throw 'Topology change reopened old torrent routes.' }
    Write-Output 'PASS network topology changes latch the session closed until restart'
    if ((GetJson '/api/controllarr/stats').connection_limit -ne 75 -or (GetJson '/api/controllarr/settings').connection_limits.download_reserve_percent -ne 50) {
        throw 'Browser settings did not apply the global connection cap live or preserve the reserve'
    }
    Write-Output 'PASS browser connection limits apply live without weakening the network guard'
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
    if (!(GetJson '/api/controllarr/settings').create_torrent_subfolders -or @(TorrentRows)[0].save_path -ne $expectedPath) { throw 'Storage layout settings or captured paths changed across restart.' }
    $category.create_torrent_subfolder = $false
    Invoke-WebRequest "$base/api/controllarr/categories" -Method Post -Body ($category | ConvertTo-Json) -ContentType 'application/json' -WebSession $session -UseBasicParsing | Out-Null
    if (@(TorrentRows)[0].save_path -ne $expectedPath) { throw 'Disabling category subfolders moved an existing torrent.' }
    Write-Output 'PASS global/category layout settings and captured torrent paths survive restart without reorganizing existing files'
    Write-Output 'PASS advanced controls and encrypted proxy secret survive process restart'
    $otherHash = 'b' * 40
    Invoke-WebRequest "$base/api/v2/torrents/add" -Method Post -Body @{ urls="magnet:?xt=urn:btih:$otherHash" } -WebSession $session -UseBasicParsing | Out-Null
    Invoke-WebRequest "$base/api/v2/torrents/setCategory" -Method Post -Body @{ hashes=$otherHash; category='storage-fixture' } -WebSession $session -UseBasicParsing | Out-Null
    $assigned = @(TorrentRows) | Where-Object hash -eq $otherHash
    if ($assigned.category -ne 'storage-fixture') { throw 'API category reassignment was not reflected in the live engine.' }
    Invoke-WebRequest "$base/api/v2/torrents/delete" -Method Post -Body @{ hashes="$hash|$($hash.ToUpperInvariant())"; deleteFiles='false' } -WebSession $session -UseBasicParsing | Out-Null
    $remaining = @(TorrentRows)
    if ($remaining.Count -ne 1 -or $remaining[0].hash -ne $otherHash -or $remaining[0].priority -ne 1) { throw 'Batch API deletion did not preserve and compact the unselected torrent' }
    Invoke-WebRequest "$base/api/v2/torrents/delete" -Method Post -Body @{ hashes='all'; deleteFiles='true' } -WebSession $session -UseBasicParsing | Out-Null
    if (@(TorrentRows).Count -ne 0) { throw 'API hashes=all deletion did not remove the disposable library' }
    Write-Output 'PASS API deletion deduplicates selection, preserves unselected torrents and supports hashes=all'
    $freshHash = 'd' * 40
    Invoke-WebRequest "$base/api/v2/torrents/add" -Method Post -Body @{ urls="magnet:?xt=urn:btih:$freshHash" } -WebSession $session -UseBasicParsing | Out-Null
    Invoke-WebRequest "$base/api/v2/torrents/pause" -Method Post -Body @{ hashes=$freshHash } -WebSession $session -UseBasicParsing | Out-Null
    $fresh = @(TorrentRows)
    if ($fresh.Count -ne 1 -or $fresh[0].priority -ne 1 -or $fresh[0].state -notlike 'paused*') { throw 'Post-deletion add did not reset rank or stop metadata on pause' }
    Write-Output 'PASS adding after emptying the queue restarts at 1; metadata pause stops actual transfer activity'
    Invoke-WebRequest "$base/api/v2/torrents/delete" -Method Post -Body @{ hashes='all'; deleteFiles='false' } -WebSession $session -UseBasicParsing | Out-Null
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
