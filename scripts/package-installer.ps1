param(
    [Parameter(Mandatory=$true)][string]$PayloadDirectory,
    [Parameter(Mandatory=$true)][ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$CompilerPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version' }
$PayloadDirectory = [IO.Path]::GetFullPath($PayloadDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath (Join-Path $PayloadDirectory 'Controllarr.exe'))) { throw 'Complete published app folder is required' }
if (!(Test-Path -LiteralPath $CompilerPath)) { throw 'Install Inno Setup 6 (6.7.3 tested) or pass -CompilerPath' }
$output = Join-Path $OutputDirectory "Controllarr-$Version-$RuntimeIdentifier-Setup.exe"
if (Test-Path -LiteralPath $output) { throw 'Installer already exists; use a new output folder' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& $CompilerPath /Qp "/DAppVersion=$Version" "/DRuntime=$RuntimeIdentifier" "/DPayloadDir=$PayloadDirectory" "/O$OutputDirectory" (Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\Controllarr.iss')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $output)) { throw "Installer compilation failed: $RuntimeIdentifier" }
Write-Output "Packaged $output"
