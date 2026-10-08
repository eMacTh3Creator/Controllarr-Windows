param(
    [Parameter(Mandatory=$true)][string]$PackagesDirectory,
    [string]$CopyToDirectory = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$lab = Join-Path $env:LOCALAPPDATA 'ControllarrNativeLab'
$packages = [IO.Path]::GetFullPath($PackagesDirectory)
if (!$packages.StartsWith($lab + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package tests must use the disposable ControllarrNativeLab.'
}
$root = Split-Path $PSScriptRoot -Parent
$project = [xml](Get-Content (Join-Path $root 'src\Controllarr.App\Controllarr.App.csproj'))
$version = ($project.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version
$sums = @(Get-Content (Join-Path $packages 'SHA256SUMS.txt'))
if ($sums.Count -ne 4) { throw 'Expected two installers and two portable archives.' }
foreach ($line in $sums) {
    $parts = $line -split '  ', 2
    if ($parts.Count -ne 2 -or [IO.Path]::GetFileName($parts[1]) -ne $parts[1]) { throw 'Invalid checksum entry.' }
    if ((Get-FileHash (Join-Path $packages $parts[1]) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $parts[0]) {
        throw 'Package checksum mismatch.'
    }
}
foreach ($runtime in @('win-x64', 'win-arm64')) {
    & (Join-Path $PSScriptRoot 'test-windows-installer.ps1') -InstallerPath (Join-Path $packages "Controllarr-$version-$runtime-Setup.exe") -RuntimeIdentifier $runtime -Version $version
    $target = Join-Path $lab ('release-api-' + $runtime + '-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive (Join-Path $packages "Controllarr-$version-$runtime.zip") $target
    & (Join-Path $PSScriptRoot 'test-windows-network-api.ps1') -ExecutableDirectory $target
}
if ($CopyToDirectory) {
    New-Item -ItemType Directory -Force $CopyToDirectory | Out-Null
    foreach ($line in $sums) {
        $parts = $line -split '  ', 2
        Copy-Item (Join-Path $packages $parts[1]) $CopyToDirectory
        if ((Get-FileHash (Join-Path $CopyToDirectory $parts[1]) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $parts[0]) {
            throw 'Copied package checksum mismatch.'
        }
    }
    Copy-Item (Join-Path $packages 'SHA256SUMS.txt') $CopyToDirectory
}
Write-Output 'PASS both architecture installer/API smoke tests and all package checksums.'
