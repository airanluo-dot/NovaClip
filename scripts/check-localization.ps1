$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$zh = [xml](Get-Content (Join-Path $root "src\NovaClip.App\Strings\zh-CN\Resources.resw") -Raw)
$en = [xml](Get-Content (Join-Path $root "src\NovaClip.App\Strings\en-US\Resources.resw") -Raw)
$zhKeys = @($zh.root.data | ForEach-Object name | Sort-Object)
$enKeys = @($en.root.data | ForEach-Object name | Sort-Object)
$difference = Compare-Object $zhKeys $enKeys
if ($difference) { $difference | Format-Table | Out-String | Write-Error; throw "LOCALIZATION_KEY_MISMATCH" }

$literalPattern = '(Text|Content|Header|PlaceholderText|Title|ToolTipService\.ToolTip)="(?!\{|\{Binding|\{ThemeResource|\{StaticResource)[^"]+"'
$xamlViolations = Get-ChildItem (Join-Path $root "src\NovaClip.App") -Recurse -Filter *.xaml | Select-String -Pattern $literalPattern
if ($xamlViolations) { $xamlViolations | ForEach-Object { Write-Error $_ }; throw "LOCALIZATION_XAML_LITERAL" }

$codePattern = '\.(Text|Content|Title)\s*=\s*"[^"\r\n]+"'
$codeViolations = Get-ChildItem (Join-Path $root "src\NovaClip.App\Pages") -Recurse -Filter *.cs | Select-String -Pattern $codePattern
if ($codeViolations) { $codeViolations | ForEach-Object { Write-Error $_ }; throw "LOCALIZATION_CODE_LITERAL" }
Write-Host "Localization gates passed: $($zhKeys.Count) keys."
