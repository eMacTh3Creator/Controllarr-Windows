param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\packages'),
    [string]$CompilerPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Push-Location $root
try {
    & dotnet build Controllarr.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    & dotnet run --project tests/Controllarr.Desktop.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Desktop logic/engine tests failed' }
    & dotnet run --project tests/Controllarr.Windows.UI.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'WPF tests failed' }
    & (Join-Path $PSScriptRoot 'test-profile-import.ps1')
    $project = [xml](Get-Content src/Controllarr.App/Controllarr.App.csproj)
    $version = ($project.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $checksums = @()
    foreach ($runtime in @('win-x64', 'win-arm64')) {
        $stage = Join-Path $OutputDirectory "stage-$runtime-$([guid]::NewGuid().ToString('N'))"
        & dotnet publish src/Controllarr.App/Controllarr.App.csproj -c Release -r $runtime --self-contained true -p:PublishSingleFile=false -o $stage --nologo
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $runtime" }
        foreach ($guide in @('INSTALL.md', 'DESKTOP.md', 'STORAGE.md', 'NETWORKING.md', 'OPERATIONS.md', 'PERFORMANCE.md', 'NATIVE_DESKTOP_VALIDATION.md', 'STALL_AUDIT.md')) {
            Copy-Item (Join-Path 'docs' $guide) (Join-Path $stage $guide)
        }
        Copy-Item LICENSE (Join-Path $stage 'LICENSE')
        Copy-Item THIRD_PARTY_NOTICES.md (Join-Path $stage 'THIRD_PARTY_NOTICES.md')
        $notes = "RELEASE_NOTES_v$version.md"
        if (Test-Path $notes) { Copy-Item $notes (Join-Path $stage $notes) }
        $archive = Join-Path $OutputDirectory "Controllarr-$version-$runtime.zip"
        if (Test-Path $archive) { throw "Package already exists: $archive. Choose another output folder to preserve the previous build." }
        Compress-Archive -Path "$stage\*" -DestinationPath $archive
        $checksums += "$((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($archive))"
        Write-Output "Packaged $archive"
        & (Join-Path $PSScriptRoot 'package-installer.ps1') -PayloadDirectory $stage -RuntimeIdentifier $runtime -Version $version -OutputDirectory $OutputDirectory -CompilerPath $CompilerPath
        $setup = Join-Path $OutputDirectory "Controllarr-$version-$runtime-Setup.exe"
        $checksums += "$((Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($setup))"
        # Only this invocation's generated staging folder is removed.
        Remove-Item $stage -Recurse -Force
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n", [Text.Encoding]::ASCII)
} finally { Pop-Location }
