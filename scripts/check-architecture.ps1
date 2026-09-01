$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$contracts = Get-ChildItem (Join-Path $root "src\NovaClip.Contracts") -Recurse -File -Include *.cs,*.csproj |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Get-Content -Raw
foreach ($forbidden in @("Microsoft.UI", "Microsoft.Web.WebView2", "Microsoft.Data.Sqlite", "FFMpegCore")) { if ($contracts -match $forbidden) { throw "CONTRACTS_FORBIDDEN_DEPENDENCY:$forbidden" } }
$pages = Get-ChildItem (Join-Path $root "src\NovaClip.App\Pages") -Recurse -Filter *.cs | Get-Content -Raw
foreach ($forbidden in @("new HttpClient", "File.WriteAllText", "SqliteConnection")) { if ($pages -match [regex]::Escape($forbidden)) { throw "APP_LAYER_VIOLATION:$forbidden" } }
Write-Host "Architecture gates passed."
