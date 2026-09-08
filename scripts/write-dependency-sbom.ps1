param(
    [string]$ProjectPath = "NovaClip.slnx",
    [string]$OutputPath = "artifacts\dependencies\novaclip.cdx.json"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$resolvedProject = if ([IO.Path]::IsPathRooted($ProjectPath)) { $ProjectPath } else { Join-Path $root $ProjectPath }
if (-not (Test-Path $resolvedProject)) { throw "SBOM_PROJECT_NOT_FOUND:" + $resolvedProject }

$jsonText = (& dotnet list $resolvedProject package --include-transitive --format json 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($jsonText)) { throw "SBOM_PACKAGE_INVENTORY_FAILED" }
try { $report = $jsonText | ConvertFrom-Json } catch { throw "SBOM_PACKAGE_INVENTORY_INVALID_JSON" }

$components = @{}
foreach ($project in @($report.projects)) {
    foreach ($framework in @($project.frameworks)) {
        foreach ($package in @($framework.topLevelPackages)) {
            $id = [string]$package.id
            $version = [string]$package.resolvedVersion
            if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) { continue }
            $key = $id + "@" + $version
            $components[$key] = [ordered]@{
                type = "library"
                "bom-ref" = "pkg:nuget/" + $id + "@" + $version
                name = $id
                version = $version
                purl = "pkg:nuget/" + [Uri]::EscapeDataString($id) + "@" + [Uri]::EscapeDataString($version)
                scope = "required"
            }
        }

        foreach ($package in @($framework.transitivePackages)) {
            $id = [string]$package.id
            $version = [string]$package.resolvedVersion
            if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) { continue }
            $key = $id + "@" + $version
            if (-not $components.ContainsKey($key)) {
                $components[$key] = [ordered]@{
                    type = "library"
                    "bom-ref" = "pkg:nuget/" + $id + "@" + $version
                    name = $id
                    version = $version
                    purl = "pkg:nuget/" + [Uri]::EscapeDataString($id) + "@" + [Uri]::EscapeDataString($version)
                    scope = "optional"
                }
            }
        }
    }
}

$componentList = @($components.Values | Sort-Object name, version)
if ($componentList.Count -eq 0) { throw "SBOM_PACKAGE_INVENTORY_EMPTY" }

$absoluteOutput = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $root $OutputPath }
$parent = Split-Path -Parent $absoluteOutput
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$bom = [ordered]@{
    bomFormat = "CycloneDX"
    specVersion = "1.5"
    version = 1
    metadata = [ordered]@{
        timestamp = [DateTime]::UtcNow.ToString("O")
        tools = @(
            [ordered]@{
                vendor = "NovaClip"
                name = "write-dependency-sbom.ps1"
                version = "1"
            }
        )
    }
    components = $componentList
}
$bom | ConvertTo-Json -Depth 10 | Set-Content -Path $absoluteOutput -Encoding utf8
Write-Host "Dependency SBOM: $absoluteOutput"
