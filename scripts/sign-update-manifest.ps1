param(
    [string]$InputRoot = "artifacts\windows"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:NOVACLIP_UPDATE_SIGNING_KEY_PEM)) {
    throw "RELEASE_SIGNING_KEY_MISSING"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
[xml]$versionProps = Get-Content (Join-Path $repoRoot "version.props") -Raw
$version = [string]$versionProps.Project.PropertyGroup.NovaClipVersion
$runtimeIdentifier = [string]$versionProps.Project.PropertyGroup.NovaClipRuntimeIdentifier
if ([string]::IsNullOrWhiteSpace($version) -or $runtimeIdentifier -ne "win-x64") { throw "VERSION_PROPS_INVALID" }

$portable = @(Get-ChildItem -Path $InputRoot -File -Filter "*-portable.zip")
$setup = @(Get-ChildItem -Path $InputRoot -File -Filter "*-setup.exe")
if ($portable.Count -ne 1 -or $setup.Count -ne 1) { throw "RELEASE_ASSET_LAYOUT_INVALID" }

function Get-Asset([System.IO.FileInfo]$file, [string]$packageType) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{
        name = $file.Name
        sha256 = $hash
        size = [int64]$file.Length
        runtimeIdentifier = $runtimeIdentifier
        packageType = $packageType
    }
}

$manifest = [ordered]@{
    version = $version
    channel = $(if ($version.Contains("-")) { "preview" } else { "stable" })
    keyId = "novaclip-beta7-2026"
    assets = @(
        (Get-Asset $setup[0] "setup"),
        (Get-Asset $portable[0] "portable")
    )
}
$json = $manifest | ConvertTo-Json -Depth 5 -Compress
$manifestPath = Join-Path $InputRoot "novaclip-update-manifest.json"
$signaturePath = Join-Path $InputRoot "novaclip-update-manifest.sig"
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($manifestPath, $json, $utf8)

$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($env:NOVACLIP_UPDATE_SIGNING_KEY_PEM)
    $signature = $rsa.SignData(
        [System.Text.Encoding]::UTF8.GetBytes($json),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [System.IO.File]::WriteAllText($signaturePath, [Convert]::ToBase64String($signature), $utf8)
}
finally {
    $rsa.Dispose()
}

Write-Host "Signed update manifest: $manifestPath"
