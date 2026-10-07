param(
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$TargetDirectory,
    [switch]$SkipCredentials,
    [switch]$ValidateOnly,
    [string]$ResultFile
)
$ErrorActionPreference = 'Stop'
$managed = @('state.json', 'credentials.dat', 'desktop-layout.json', 'rss-history.json', 'resume')
$stage = $null
$installed = @()
$backedUp = @()
$backup = $null
function Assert-NoLinks([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Profile paths cannot contain links or junctions.' }
        $item = $item.Parent
    }
}
function Read-Json([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 32MB) { throw 'A profile JSON file is too large.' }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) }
    catch { throw 'A profile JSON file is invalid.' }
}
try {
    $source = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
    $target = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\')
    if ($source -eq $target -or $source.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $target.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose a separate source profile. Use Keep existing settings for the current profile.'
    }
    if (!(Test-Path -LiteralPath (Join-Path $source 'state.json') -PathType Leaf)) { throw 'Choose a profile folder containing state.json, not the application or download folder.' }
    Assert-NoLinks $source
    $parent = Split-Path $target -Parent
    if (!(Test-Path -LiteralPath $parent)) { throw 'The target profile parent folder does not exist.' }
    Assert-NoLinks $parent
    if (Test-Path -LiteralPath $target) { Assert-NoLinks $target }
    $state = Read-Json (Join-Path $source 'state.json')
    if ($null -eq $state.settings -or $state.settings -isnot [pscustomobject]) { throw 'The profile must contain a settings object.' }
    $entries = @()
    $files = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
    foreach ($name in $managed) {
        $path = Join-Path $source $name
        if (Test-Path -LiteralPath $path) {
            Assert-NoLinks $path
            if ($name -ne 'resume' -and (Get-Item -LiteralPath $path) -isnot [IO.FileInfo]) { throw 'A profile file was replaced by a directory.' }
            if ($name -eq 'resume') {
                if (!(Test-Path -LiteralPath $path -PathType Container)) { throw 'The resume entry must be a directory.' }
                # Reject links before recursing, including directory junctions.
                $queue = New-Object 'System.Collections.Generic.Queue[string]'
                $queue.Enqueue($path)
                while ($queue.Count -gt 0) {
                    foreach ($child in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
                        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Resume data cannot contain links or junctions.' }
                        if ($child.PSIsContainer) { $queue.Enqueue($child.FullName) } else { $files.Add($child) }
                        if ($files.Count -gt 100000) { throw 'The profile contains too many files.' }
                    }
                }
            } else { $files.Add((Get-Item -LiteralPath $path)) }
            if ($name -ne 'credentials.dat' -or !$SkipCredentials) { $entries += $name }
        }
        $existing = Join-Path $target $name
        if (Test-Path -LiteralPath $existing) { Assert-NoLinks $existing }
    }
    if (($files | Measure-Object Length -Sum).Sum -gt 2GB) { throw 'Profile metadata exceeds the 2 GiB import limit.' }
    foreach ($name in @('desktop-layout.json', 'rss-history.json')) {
        if (Test-Path -LiteralPath (Join-Path $source $name)) { $null = Read-Json (Join-Path $source $name) }
    }
    if (!$SkipCredentials -and (Test-Path -LiteralPath (Join-Path $source 'credentials.dat'))) {
        Add-Type -AssemblyName System.Security
        $credentials = Read-Json (Join-Path $source 'credentials.dat')
        if ($credentials -isnot [pscustomobject]) { throw 'The credentials file is invalid.' }
        foreach ($property in $credentials.PSObject.Properties) {
            $plain = $null
            try {
                $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($property.Value), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            } catch { throw 'Saved passwords cannot be opened by this Windows account. Uncheck Import saved passwords and enter them again after installation.' }
            finally { if ($null -ne $plain) { [Array]::Clear($plain, 0, $plain.Length) } }
        }
    }
    if ($ValidateOnly) {
        if ($ResultFile) { 'Profile is valid.' | Set-Content -LiteralPath $ResultFile -Encoding UTF8 }
        exit 0
    }
    $id = [guid]::NewGuid().ToString('N')
    $stage = Join-Path $parent "Controllarr-import-$id"
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($name in $entries) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $stage $name) -Recurse }
    foreach ($file in [IO.Directory]::EnumerateFiles($stage, '*', [IO.SearchOption]::AllDirectories)) {
        # Read-only backups must not make the installed profile unwritable.
        $attributes = [IO.File]::GetAttributes($file)
        [IO.File]::SetAttributes($file, ($attributes -band (-bnot [IO.FileAttributes]::ReadOnly)))
    }
    if ($SkipCredentials) {
        # Do not expose fallback login credentials to the LAN on first launch.
        $state.settings | Add-Member -NotePropertyName web_ui_host -NotePropertyValue '127.0.0.1' -Force
        $state | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $stage 'state.json') -Encoding UTF8
    }
    $backup = Join-Path (Join-Path $parent 'Controllarr-profile-backups') ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $id)
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($name in $managed) {
        $path = Join-Path $target $name
        if (Test-Path -LiteralPath $path) {
            Move-Item -LiteralPath $path -Destination (Join-Path $backup $name)
            $backedUp += $name
        }
    }
    foreach ($name in $entries) {
        Move-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $target $name)
        $installed += $name
    }
    if ($ResultFile) { "Profile imported. Previous metadata backup: $backup" | Set-Content -LiteralPath $ResultFile -Encoding UTF8 }
    Write-Output "Profile imported. Previous metadata backup: $backup"
} catch {
    $message = $_.Exception.Message
    if ($backup) {
        try {
            foreach ($name in $installed) { Remove-Item -LiteralPath (Join-Path $target $name) -Recurse -Force }
            foreach ($name in $backedUp) { Move-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $target $name) }
        } catch { $message += " Restore the original metadata from $backup before starting Controllarr." }
    }
    if ($ResultFile) { $message | Set-Content -LiteralPath $ResultFile -Encoding UTF8 }
    Write-Error $message -ErrorAction Continue
    exit 1
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
