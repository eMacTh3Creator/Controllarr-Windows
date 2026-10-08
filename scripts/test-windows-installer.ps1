param([Parameter(Mandatory=$true)][string]$InstallerPath,
    [ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier,
    [string]$Version = '2.3.0')
$ErrorActionPreference = 'Stop'
$id = [guid]::NewGuid().ToString('N')
$root = Join-Path $env:LOCALAPPDATA "ControllarrNativeLab\installer-tests-$id"
$app = Join-Path $root 'app'
$profile = Join-Path $root 'profile'
$source = Join-Path $root 'old-profile'
New-Item -ItemType Directory -Path $profile, $source -Force | Out-Null
@{ settings = @{ web_ui_port = 18793 }; categories = @(@{ name = 'Installer imported' }) } | ConvertTo-Json -Depth 5 | Set-Content "$source\state.json" -Encoding UTF8
@{ settings = @{ web_ui_port = 18794 } } | ConvertTo-Json | Set-Content "$profile\state.json" -Encoding UTF8
function Install-Test([string]$Import) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-', "/DIR=`"$app`"", "/LABID=$id", "/PROFILEDIR=`"$profile`"", "/LOG=`"$root\install.log`"")
    if ($Import) { $arguments += "/IMPORTPROFILE=`"$Import`"" }
    $process = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Installer failed ($($process.ExitCode)); see $root\install.log" }
}
Install-Test ''
if (!(Test-Path "$app\Controllarr.exe") -or !(Test-Path "$app\Controllarr.deps.json") -or !(Test-Path "$app\unins000.exe")) { throw 'Installer did not install the complete self-contained folder and uninstaller' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo("$app\Controllarr.exe").ProductVersion -notlike "$Version*") { throw 'Installed app version does not match release' }
if ((Get-Content "$profile\state.json" -Raw | ConvertFrom-Json).settings.web_ui_port -ne 18794) { throw 'Keep settings installation altered the existing profile' }
Write-Output "PASS $RuntimeIdentifier installer preserves the existing profile and installs the complete app"
Install-Test $source
if ((Get-Content "$profile\state.json" -Raw | ConvertFrom-Json).settings.web_ui_port -ne 18793) { throw 'Installer import did not migrate settings' }
if (@(Get-ChildItem "$root\Controllarr-profile-backups" -Directory).Count -ne 1) { throw 'Installer import did not create a metadata backup' }
Write-Output "PASS $RuntimeIdentifier installer upgrades in place and imports the selected previous profile with a backup"
$uninstall = Start-Process -FilePath "$app\unins000.exe" -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
if ($uninstall.ExitCode -ne 0 -or (Test-Path "$app\Controllarr.exe") -or !(Test-Path "$profile\state.json")) { throw 'Uninstaller did not preserve the profile while removing application files' }
Write-Output "PASS $RuntimeIdentifier uninstall keeps profile metadata; fixtures retained at $root"
