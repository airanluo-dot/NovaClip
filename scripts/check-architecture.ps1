$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$versionPropsPath = Join-Path $root "version.props"
$versionProps = Get-Content $versionPropsPath -Raw
$assemblyVersionMatch = [regex]::Match($versionProps, '<NovaClipAssemblyVersion>([^<]+)</NovaClipAssemblyVersion>')
if (-not $assemblyVersionMatch.Success) { throw "VERSION_PROPS_INVALID" }
$assemblyVersion = $assemblyVersionMatch.Groups[1].Value
$manifestPath = Join-Path $root "src\NovaClip.App\app.manifest"
$manifestText = Get-Content $manifestPath -Raw
if ($manifestText -notmatch ('assemblyIdentity\s+version="' + [regex]::Escape($assemblyVersion) + '"')) { throw "APP_MANIFEST_VERSION_MISMATCH" }

foreach ($legacyPath in @(
    (Join-Path $root "BiliNative.sln"),
    (Join-Path $root "src\BiliNative.App"),
    (Join-Path $root "src\BiliNative.Core"),
    (Join-Path $root "src\BiliNative.Infrastructure"),
    (Join-Path $root "src\BiliNative.Updater"),
    (Join-Path $root "src\BiliNative.WebBridge"),
    (Join-Path $root "tests\BiliNative.Core.Tests"),
    (Join-Path $root "tests\BiliNative.Infrastructure.Tests"),
    (Join-Path $root "tests\BiliNative.WebBridge.Tests")
)) {
    if (Test-Path $legacyPath) { throw "LEGACY_BILINATIVE_TREE_PRESENT:" + $legacyPath }
}

$contracts = Get-ChildItem (Join-Path $root "src\NovaClip.Contracts") -Recurse -File -Include *.cs,*.csproj |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Get-Content -Raw
foreach ($forbidden in @("Microsoft.UI", "Microsoft.Web.WebView2", "Microsoft.Data.Sqlite", "FFMpegCore")) { if ($contracts -match $forbidden) { throw "CONTRACTS_FORBIDDEN_DEPENDENCY:$forbidden" } }
$pages = Get-ChildItem (Join-Path $root "src\NovaClip.App\Pages") -Recurse -Filter *.cs | Get-Content -Raw
foreach ($forbidden in @("new HttpClient", "File.WriteAllText", "SqliteConnection")) { if ($pages -match [regex]::Escape($forbidden)) { throw "APP_LAYER_VIOLATION:$forbidden" } }
Write-Host "Architecture gates passed."
