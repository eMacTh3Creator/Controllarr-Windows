$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP "ControllarrProfileTests-$([guid]::NewGuid().ToString('N'))"
$source = Join-Path $root 'old-profile'
$target = Join-Path $root 'new-profile'
$helper = Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\ImportProfile.ps1'
New-Item -ItemType Directory -Path "$source\resume\torrents", $target, "$source\downloads" -Force | Out-Null
@{ settings = @{ web_ui_port = 18793; web_ui_host = '0.0.0.0'; torrent_queueing = @{ enabled = $true } }; categories = @(@{ name = 'Imported' }) } | ConvertTo-Json -Depth 8 | Set-Content "$source\state.json" -Encoding UTF8
@{ settings = @{ web_ui_port = 18794 } } | ConvertTo-Json | Set-Content "$target\state.json" -Encoding UTF8
'metadata fixture' | Set-Content "$source\resume\torrents\fixture.torrent"
'do not import payload' | Set-Content "$source\downloads\payload.bin"
Add-Type -AssemblyName System.Security
$protected = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('isolated-profile-test-secret'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
@{ webui_password = $protected } | ConvertTo-Json | Set-Content "$source\credentials.dat" -Encoding UTF8
function Import-Test([string[]]$Extra = @()) {
    $id = [guid]::NewGuid().ToString('N')
    $arguments = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',"`"$helper`"",'-SourceDirectory',"`"$source`"",'-TargetDirectory',"`"$target`"") + $Extra
    # Invalid-input cases intentionally return errors; capture them without
    # turning native stderr into a terminating parent-shell exception.
    $process = Start-Process powershell.exe -ArgumentList $arguments -RedirectStandardOutput "$root\stdout-$id.log" -RedirectStandardError "$root\stderr-$id.log" -Wait -PassThru
    return $process.ExitCode
}
function Check([bool]$Value, [string]$Message) {
    if (!$Value) { throw $Message }
    Write-Output "PASS $Message"
}
$before = (Get-FileHash "$target\state.json").Hash
$sourceState = Get-Item "$source\state.json"
$sourceState.IsReadOnly = $true
$output = Import-Test @('-ValidateOnly')
Check ($output[-1] -eq 0 -and (Get-FileHash "$target\state.json").Hash -eq $before) 'Import validation does not change the target profile'
$output = Import-Test
Check ($output[-1] -eq 0 -and (Get-Content "$target\state.json" -Raw | ConvertFrom-Json).settings.web_ui_port -eq 18793) 'Import preserves previous settings and categories'
Check ((Get-Item "$source\state.json").IsReadOnly -and !(Get-Item "$target\state.json").IsReadOnly) 'Read-only backup attributes stay on the source, not the writable imported profile'
$sourceState.IsReadOnly = $false
Check ((Get-Content "$target\credentials.dat" -Raw | ConvertFrom-Json).webui_password -eq $protected) 'Same-user DPAPI credentials copy without re-encryption or prompts'
Check ((Test-Path "$target\resume\torrents\fixture.torrent") -and !(Test-Path "$target\downloads") -and (Test-Path "$source\downloads\payload.bin")) 'Import copies resume metadata but never copies or deletes payload files'
$backups = @(Get-ChildItem "$root\Controllarr-profile-backups" -Directory)
Check ($backups.Count -eq 1 -and (Get-Content (Join-Path $backups[0].FullName 'state.json') -Raw | ConvertFrom-Json).settings.web_ui_port -eq 18794) 'Import backs up the previous target metadata'
$before = (Get-FileHash "$target\state.json").Hash
$lock = [IO.File]::Open("$target\resume\torrents\fixture.torrent", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
try { $output = Import-Test }
finally { $lock.Dispose() }
Check ($output[-1] -ne 0 -and (Get-FileHash "$target\state.json").Hash -eq $before -and (Test-Path "$target\credentials.dat")) 'A locked target file rolls back metadata moved before the failed replacement'
'{"webui_password":"not-valid-dpapi"}' | Set-Content "$source\credentials.dat"
$before = (Get-FileHash "$target\state.json").Hash
$output = Import-Test @('-ValidateOnly')
Check ($output[-1] -ne 0 -and (Get-FileHash "$target\state.json").Hash -eq $before) 'Undecryptable passwords are rejected before the target is changed'
$output = Import-Test @('-SkipCredentials')
Check ($output[-1] -eq 0 -and !(Test-Path "$target\credentials.dat") -and (Get-Content "$target\state.json" -Raw | ConvertFrom-Json).settings.web_ui_host -eq '127.0.0.1') 'Import without passwords drops stale credentials and restricts first-run WebUI access to loopback'
'not-json' | Set-Content "$source\state.json"
$before = (Get-FileHash "$target\state.json").Hash
$output = Import-Test
Check ($output[-1] -ne 0 -and (Get-FileHash "$target\state.json").Hash -eq $before) 'Malformed settings do not overwrite an existing profile'
Write-Output "Profile import fixtures retained at $root"
