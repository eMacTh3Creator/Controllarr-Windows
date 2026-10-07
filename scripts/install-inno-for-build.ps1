param([Parameter(Mandatory=$true)][string]$DestinationDirectory)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$DestinationDirectory = [IO.Path]::GetFullPath($DestinationDirectory)
New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
$download = Join-Path $DestinationDirectory 'innosetup-6.7.3.exe'
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download -UseBasicParsing
if ((Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732') { throw 'Inno Setup bootstrap checksum does not match the pinned release' }
if ((Get-AuthenticodeSignature $download).Status -ne 'Valid') { throw 'Inno Setup bootstrap signature is not valid' }
$compilerDirectory = Join-Path $DestinationDirectory 'compiler'
$process = Start-Process $download -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',"/DIR=`"$compilerDirectory`"" -PassThru -Wait
if ($process.ExitCode -ne 0 -or !(Test-Path "$compilerDirectory\ISCC.exe")) { throw 'Inno Setup bootstrap failed' }
Write-Output "$compilerDirectory\ISCC.exe"
