param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts\windows"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
[xml]$versionProps = Get-Content (Join-Path $repoRoot "version.props") -Raw
$version = [string]$versionProps.Project.PropertyGroup.NovaClipVersion
$runtimeIdentifier = [string]$versionProps.Project.PropertyGroup.NovaClipRuntimeIdentifier
if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($runtimeIdentifier)) { throw "VERSION_PROPS_INVALID" }

$publishRoot = Join-Path $repoRoot ("publish\" + $runtimeIdentifier)
$artifactBase = "NovaClip-" + $version + "-" + $runtimeIdentifier
$portableRoot = Join-Path $repoRoot ($OutputRoot + "\" + $artifactBase + "-portable")
$portableZip = Join-Path $repoRoot ($OutputRoot + "\" + $artifactBase + "-portable.zip")
$manifestName = "novaclip-package-manifest.json"

New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot $OutputRoot) | Out-Null
if (Test-Path $publishRoot) { Remove-Item -Recurse -Force $publishRoot }
if (Test-Path $portableRoot) { Remove-Item -Recurse -Force $portableRoot }
if (Test-Path $portableZip) { Remove-Item -Force $portableZip }

$appPublishArgs = @("--configuration", $Configuration, "--framework", "net10.0-windows10.0.17763.0", "--runtime", $runtimeIdentifier, "--self-contained", "true", "-p:Platform=x64", "-p:WindowsAppSDKSelfContained=true", "-o", $publishRoot)
dotnet publish (Join-Path $repoRoot "src\NovaClip.App\NovaClip.App.csproj") @appPublishArgs

$updaterPublishArgs = @("--configuration", $Configuration, "--framework", "net10.0", "--runtime", $runtimeIdentifier, "--self-contained", "true", "-o", $publishRoot)
dotnet publish (Join-Path $repoRoot "src\NovaClip.Updater\NovaClip.Updater.csproj") @updaterPublishArgs

New-Item -ItemType Directory -Force -Path $portableRoot | Out-Null
Copy-Item (Join-Path $publishRoot "*") $portableRoot -Recurse -Force
Set-Content -Path (Join-Path $portableRoot "portable.marker") -Value "NovaClip portable build" -Encoding utf8

$files = @(
    Get-ChildItem -Path $portableRoot -File -Recurse |
        Where-Object { $_.Name -ne $manifestName } |
        ForEach-Object {
            $relative = $_.FullName.Substring($portableRoot.Length).TrimStart('\','/')
            [ordered]@{
                path = $relative.Replace('\','/')
                size = [int64]$_.Length
                sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
$manifest = [ordered]@{
    schemaVersion = 1
    product = "NovaClip"
    version = $version
    runtimeIdentifier = $runtimeIdentifier
    files = $files
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $portableRoot $manifestName) -Encoding utf8

Compress-Archive -Path (Join-Path $portableRoot "*") -DestinationPath $portableZip -CompressionLevel Optimal
Write-Host "Portable package: $portableZip"
