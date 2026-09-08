$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root "Directory.Packages.props"
$props = Get-Content $propsPath -Raw
if ($props -notmatch "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>") { throw "DEPENDENCY_POLICY_CENTRAL_VERSIONS_REQUIRED" }
$buildProps = Get-Content (Join-Path $root "Directory.Build.props") -Raw
if ($buildProps -notmatch "<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>") { throw "DEPENDENCY_POLICY_LOCKFILE_REQUIRED" }
if ($buildProps -notmatch "<LangVersion>14.0</LangVersion>") { throw "DEPENDENCY_POLICY_LANGUAGE_VERSION_REQUIRED" }
$globalJson = Get-Content (Join-Path $root "global.json") -Raw
if ($globalJson -notmatch '"rollForward"\s*:\s*"disable"') { throw "DEPENDENCY_POLICY_SDK_ROLLFORWARD_REQUIRED" }

$versions = [regex]::Matches($props, 'Version="([^"]+)"')
foreach ($match in $versions) {
    if ($match.Groups[1].Value -match "[*]|^\s*$|[-+](preview|alpha|beta)") { throw "DEPENDENCY_POLICY_FLOATING_OR_PREVIEW_VERSION:" + $match.Groups[1].Value }
}
$projects = Get-ChildItem (Join-Path $root "src"), (Join-Path $root "tests") -Recurse -File -Filter *.csproj
foreach ($project in $projects) {
    $text = Get-Content $project.FullName -Raw
    if ($text -match '<PackageReference[^>]+Version=') { throw "DEPENDENCY_POLICY_PROJECT_VERSION_OVERRIDE:" + $project.FullName }
}
$workflowFiles = Get-ChildItem (Join-Path $root ".github\workflows") -File -Filter *.yml
foreach ($workflow in $workflowFiles) {
    $text = Get-Content $workflow.FullName -Raw
    foreach ($match in [regex]::Matches($text, 'uses:\s*([^\s@]+)@([^\s#]+)')) {
        if ($match.Groups[2].Value -notmatch '^[0-9a-f]{40}$') { throw "DEPENDENCY_POLICY_ACTION_NOT_PINNED:" + $match.Groups[1].Value }
    }
}
Write-Host "Dependency policy passed: centrally pinned packages, lock-file restore enabled, immutable Actions, no project-level version overrides and no preview/floating package versions."
