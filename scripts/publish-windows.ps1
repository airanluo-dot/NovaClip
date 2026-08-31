param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts\windows"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot "publish\win-x64"
$portableRoot = Join-Path $repoRoot "$OutputRoot\NovaClip-win-x64-portable"
$portableZip = Join-Path $repoRoot "$OutputRoot\NovaClip-win-x64-portable.zip"

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
if (Test-Path $publishRoot) { Remove-Item -Recurse -Force $publishRoot }
if (Test-Path $portableRoot) { Remove-Item -Recurse -Force $portableRoot }
if (Test-Path $portableZip) { Remove-Item -Force $portableZip }

dotnet publish (Join-Path $repoRoot "src\NovaClip.App\NovaClip.App.csproj") `
    --configuration $Configuration --framework net10.0-windows10.0.17763.0 --runtime win-x64 --self-contained true `
    -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -o $publishRoot

dotnet publish (Join-Path $repoRoot "src\NovaClip.Updater\NovaClip.Updater.csproj") `
    --configuration $Configuration --framework net10.0 --runtime win-x64 --self-contained true `
    -o $publishRoot

New-Item -ItemType Directory -Force -Path $portableRoot | Out-Null
Copy-Item (Join-Path $publishRoot "*") $portableRoot -Recurse -Force
Set-Content -Path (Join-Path $portableRoot "portable.marker") -Value "NovaClip portable build" -Encoding utf8
Compress-Archive -Path (Join-Path $portableRoot "*") -DestinationPath $portableZip -CompressionLevel Optimal

Write-Host "Portable package: $portableZip"
