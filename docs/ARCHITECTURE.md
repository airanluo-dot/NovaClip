# NovaClip architecture

## Boundary

`NovaClip.Contracts` contains the stable capability and transport contracts shared by the application layers.

`NovaClip.Core` contains domain models, state transitions, filenames, retry policy and update contracts. It has no Windows UI dependency.

`NovaClip.Bilibili` converts trusted, size-limited JSON from Bilibili into typed page context and media descriptors. It accepts only schema version 1 and Bilibili-origin page URLs.

`NovaClip.Infrastructure` owns streaming HTTP downloads, Range resume, candidate URL fallback, SQLite task/history persistence and GitHub Release update discovery.

`NovaClip.Windows` owns Windows-specific FFmpeg and update-process integration. `NovaClip.App` hosts WinUI 3, WebView2, the persistent profile, response observation and the update coordinator.

`NovaClip.Updater` is a small self-contained process. It waits for NovaClip to exit, copies the extracted portable package over the target directory, deliberately leaves the updater executable in place, and starts NovaClip again.

## Media flow

```text
Bilibili page
  → WebView2 document-created bridge reports page context
  → WebResourceResponseReceived observes small /playurl JSON
  → PlayUrlNormalizer creates MediaTrack candidates
  → HttpRangeDownloader streams each candidate to .part files
  → WindowsFfmpegService remuxes video.m4s.part + audio.m4s.part
  → history repository records the result
```

The app never downloads media through JavaScript, Blob URLs or a WASM virtual file system.

## Update flow

```text
Release API → compare semantic version → choose setup/portable asset
  → setup.exe overwrites install directory
     or
  → NovaClip.Updater.exe waits, copies portable files, restarts app
```

The update service is replaceable so a public signed manifest can be used later without changing the UI or downloader.
