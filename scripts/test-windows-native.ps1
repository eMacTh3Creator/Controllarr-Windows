param([switch]$Launch, [ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier = 'win-x64', [string]$SourceDirectory = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$source = $SourceDirectory
$lab = Join-Path $env:LOCALAPPDATA 'ControllarrNativeLab'
$working = Join-Path $lab 'source'
New-Item -ItemType Directory -Force -Path $working | Out-Null
& robocopy $source $working /E /XD bin obj publish .git /XF '*_wpftmp.csproj' /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw "Could not stage source (robocopy $LASTEXITCODE)" }
Push-Location $working
try {
    & dotnet build Controllarr.sln -c Release --nologo -v:q -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed' }
    & dotnet run --project tests/Controllarr.Desktop.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Catalog tests failed' }
    & dotnet run --project tests/Controllarr.Windows.UI.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows XAML/selection tests failed' }
    if ($Launch) {
        $profile = Join-Path $lab 'profile'
        $downloads = Join-Path $profile 'downloads'
        New-Item -ItemType Directory -Force -Path $downloads | Out-Null
        $state = Join-Path $profile 'state.json'
        if (!(Test-Path $state)) {
            @{
                settings = @{
                    default_save_path = $downloads
                    web_ui_host = '127.0.0.1'; web_ui_port = 18791
                    peer_discovery = @{ dht_enabled = $false; lsd_enabled = $false }
                    ui_preferences = @{ automatic_update_checks = $false; close_to_tray = $true }
                }
                categories = @(@{ name = 'Lab Movies'; save_path = $downloads }, @{ name = 'Lab TV'; save_path = $downloads })
            } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $state -Encoding UTF8
        }
        $env:CONTROLLARR_PROFILE_DIRECTORY = $profile
        $appDirectory = Join-Path $lab "app-$RuntimeIdentifier"
        & dotnet publish src/Controllarr.App/Controllarr.App.csproj -c Release -r $RuntimeIdentifier --self-contained true -p:PublishSingleFile=false -o $appDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'VM preview publish failed' }
        $exe = Join-Path $appDirectory 'Controllarr.exe'
        $process = Start-Process -FilePath $exe -PassThru
        Write-Output "Launched native preview PID $($process.Id). Isolated profile: $profile; API: http://127.0.0.1:18791"
    }
} finally { Pop-Location }
