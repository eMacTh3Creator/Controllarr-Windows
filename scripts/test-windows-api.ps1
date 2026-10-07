param(
    [ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier = 'win-x64',
    [ValidateRange(1,100)][int]$RestartCount = 5,
    [ValidateRange(1,10000)][int]$RequestsPerCycle = 100,
    [ValidateRange(0,3600)][int]$IdleSeconds = 30,
    [string]$ExecutableDirectory = '',
    [switch]$DisableReadyToRun,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$lab = Join-Path $env:LOCALAPPDATA 'ControllarrNativeLab'
$profile = Join-Path $lab 'profile'
$directory = if ($ExecutableDirectory) { [IO.Path]::GetFullPath($ExecutableDirectory) } else { Join-Path $lab "app-$RuntimeIdentifier" }
if (!$directory.StartsWith($lab.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'API tests only launch an executable inside the isolated ControllarrNativeLab folder.' }
$exe = Join-Path $directory 'Controllarr.exe'
if (!(Test-Path $exe)) { throw "Stage the $RuntimeIdentifier preview at $exe first." }
if (!(Test-Path (Join-Path $profile 'state.json'))) { throw 'Create the isolated lab profile with test-windows-native.ps1 first.' }
if (Get-Process Controllarr -ErrorAction SilentlyContinue) { throw 'Close Controllarr before running restart tests; no existing process will be stopped.' }
$base = 'http://127.0.0.1:18791'
$oldProfile = $env:CONTROLLARR_PROFILE_DIRECTORY
$oldReadyToRun = $env:DOTNET_ReadyToRun
$process = $null
$expectedHashes = $null
function Get-TorrentRows($session) {
    $response = Invoke-WebRequest -UseBasicParsing "$base/api/v2/torrents/info" -WebSession $session
    $text = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
    # Windows PowerShell 5.1 preserves the outer JSON array as one pipeline item.
    foreach ($row in ($text | ConvertFrom-Json)) { Write-Output $row }
}
try {
    $env:CONTROLLARR_PROFILE_DIRECTORY = $profile
    if ($DisableReadyToRun) { $env:DOTNET_ReadyToRun = '0' }
    else { Remove-Item Env:DOTNET_ReadyToRun -ErrorAction SilentlyContinue }
    for ($cycle = 1; $cycle -le $RestartCount; $cycle++) {
        $process = Start-Process $exe -PassThru
        Start-Sleep -Seconds (6 + $IdleSeconds)
        $process.Refresh()
        if ($process.HasExited) { throw "Cycle $cycle exited before API access (exit $($process.ExitCode))." }
        # These are the disposable lab's default credentials, not a production login.
        $login = Invoke-WebRequest -UseBasicParsing "$base/api/v2/auth/login" -Method Post -Body @{username='admin';password='adminadmin'} -SessionVariable session
        $loginText = if ($login.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($login.Content) } else { [string]$login.Content }
        if ($loginText.Trim() -ne 'Ok.') { throw "Cycle $cycle login failed." }
        $null = Invoke-WebRequest -UseBasicParsing "$base/" -WebSession $session
        $initial = @(Get-TorrentRows $session)
        $hashes = ($initial | ForEach-Object { $_.hash } | Sort-Object) -join ','
        if ($null -eq $expectedHashes) { $expectedHashes = $hashes }
        elseif ($hashes -ne $expectedHashes) { throw "Cycle $cycle did not restore the same torrent hashes." }
        for ($request = 0; $request -lt $RequestsPerCycle; $request++) {
            $rows = @(Get-TorrentRows $session)
            if ($rows.Count -ne $initial.Count) { throw "Cycle $cycle unexpectedly changed torrent count." }
        }
        $process.Refresh()
        if ($process.HasExited) { throw "Cycle $cycle process exited during polling." }
        Write-Output "PASS $RuntimeIdentifier cycle $cycle/${RestartCount}: login, browser index, $RequestsPerCycle polls; $($rows.Count) fixtures; PID $($process.Id)"
        if ($KeepRunning -and $cycle -eq $RestartCount) { $process = $null; break }
        $null = Invoke-RestMethod "$base/api/controllarr/shutdown" -Method Post -WebSession $session
        if (!$process.WaitForExit(35000)) { throw "Cycle $cycle did not shut down gracefully." }
        $process = $null
    }
} finally {
    $env:CONTROLLARR_PROFILE_DIRECTORY = $oldProfile
    $env:DOTNET_ReadyToRun = $oldReadyToRun
    if ($process -and !$process.HasExited) {
        Write-Warning "Test failed; stopping only its isolated lab process $($process.Id)."
        Stop-Process -Id $process.Id
    }
}
