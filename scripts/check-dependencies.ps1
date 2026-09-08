$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root "Directory.Packages.props"
$props = Get-Content $propsPath -Raw
if ($props -notmatch "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>") { throw "DEPENDENCY_POLICY_CENTRAL_VERSIONS_REQUIRED" }
$versions = [regex]::Matches($props, 'Version="([^"]+)"')
foreach ($match in $versions) {
    if ($match.Groups[1].Value -match "[*]|^\s*$|[-+](preview|alpha|beta)") { throw "DEPENDENCY_POLICY_FLOATING_OR_PREVIEW_VERSION:" + $match.Groups[1].Value }
}
$projects = Get-ChildItem (Join-Path $root "src"), (Join-Path $root "tests") -Recurse -File -Filter *.csproj
foreach ($project in $projects) {
    $text = Get-Content $project.FullName -Raw
    if ($text -match '<PackageReference[^>]+Version=') { throw "DEPENDENCY_POLICY_PROJECT_VERSION_OVERRIDE:" + $project.FullName }
}
Write-Host "Dependency policy passed: centrally pinned packages, no project-level version overrides, no preview/floating package versions."
