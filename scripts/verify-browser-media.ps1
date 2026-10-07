param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$VideoUrl,
    [Parameter(Mandatory = $true)][string]$BuildCommit,
    [switch]$BaselineHarnessOverlay
)

$ErrorActionPreference = "Stop"
$exe = (Resolve-Path $ExecutablePath).Path
$root = Split-Path -Parent $exe
$resultPath = Join-Path $root "media-acceptance\result.json"
if (Get-Process NovaClip -ErrorAction SilentlyContinue) { throw "REAL_MEDIA_ACCEPTANCE_REQUIRES_APP_CLOSED" }
if (Test-Path $resultPath) { Remove-Item $resultPath -Force }
$env:NOVACLIP_MEDIA_ACCEPTANCE_URL = $VideoUrl
$env:NOVACLIP_BUILD_COMMIT = $BuildCommit
$env:NOVACLIP_ACCEPTANCE_BASELINE = if ($BaselineHarnessOverlay) { "1" } else { "0" }
$env:NOVACLIP_MEDIA_ACCEPTANCE_FFMPEG = (Get-Command ffmpeg.exe -ErrorAction Stop).Source
# The acceptance runner emits only bounded public identity/state evidence. It uses
# real playback, WebView2 observations and the same UI enqueue command; no fixture.
$process = Start-Process $exe -PassThru
try {
    $deadline = (Get-Date).AddMinutes(6)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    } while (-not (Test-Path $resultPath) -and -not $process.HasExited -and (Get-Date) -lt $deadline)
    if (-not (Test-Path $resultPath)) {
        $reason = if ($process.HasExited) { "APP_EXITED" } else { "DEADLINE_EXCEEDED" }
        throw ("REAL_MEDIA_ACCEPTANCE_RESULT_MISSING:" + $reason)
    }
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    if ($result.buildCommit -ne $BuildCommit) { throw "REAL_MEDIA_ACCEPTANCE_BUILD_MISMATCH" }
    if ($result.status -ne "passed") {
        Write-Host ("Real embedded-browser acceptance: " + $result.status + "; " + $result.resultCode)
        throw ("REAL_MEDIA_ACCEPTANCE_" + $result.status.ToUpperInvariant() + ":" + $result.resultCode)
    }
    $probe = Get-Command ffprobe.exe -ErrorAction Stop
    if ($result.downloadDirectoryRelative -notmatch '^downloads[\\/][0-9a-f]{32}$') { throw "REAL_MEDIA_ACCEPTANCE_OUTPUT_DIRECTORY_INVALID" }
    $outputs = @(Get-ChildItem (Join-Path (Join-Path $root "media-acceptance") $result.downloadDirectoryRelative) -File |
        Where-Object { $_.Extension -in @(".mp4", ".m4a") -and (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -eq $result.outputSha256 })
    if ($outputs.Count -ne 1) { throw "REAL_MEDIA_ACCEPTANCE_OUTPUT_HASH_MISMATCH" }
    $probeJson = & $probe.Source -v error -show_entries stream=codec_type,codec_name -show_entries format=duration,size -of json $outputs[0].FullName
    if ($LASTEXITCODE -ne 0) { throw "REAL_MEDIA_ACCEPTANCE_FFPROBE_FAILED" }
    $mediaProbe = ($probeJson -join "`n") | ConvertFrom-Json
    $types = @($mediaProbe.streams | ForEach-Object { $_.codec_type })
    if ($result.media.videoTracks -gt 0 -and "video" -notin $types) { throw "REAL_MEDIA_ACCEPTANCE_VIDEO_STREAM_MISSING" }
    if ($result.media.audioTracks -gt 0 -and "audio" -notin $types) { throw "REAL_MEDIA_ACCEPTANCE_AUDIO_STREAM_MISSING" }
    if ($result.media.legacySegments -gt 0 -and "video" -notin $types) { throw "REAL_MEDIA_ACCEPTANCE_DURL_VIDEO_MISSING" }
    if ([double]$mediaProbe.format.duration -le 0) { throw "REAL_MEDIA_ACCEPTANCE_DURATION_INVALID" }
    $mediaProbe | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root "media-acceptance\output-probe.json") -Encoding utf8
    Write-Host ("Real embedded-browser acceptance passed: " + $result.version + "; " + $result.resultCode)
} finally {
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item Env:NOVACLIP_MEDIA_ACCEPTANCE_URL -ErrorAction SilentlyContinue
    Remove-Item Env:NOVACLIP_BUILD_COMMIT -ErrorAction SilentlyContinue
    Remove-Item Env:NOVACLIP_ACCEPTANCE_BASELINE -ErrorAction SilentlyContinue
    Remove-Item Env:NOVACLIP_MEDIA_ACCEPTANCE_FFMPEG -ErrorAction SilentlyContinue
}
