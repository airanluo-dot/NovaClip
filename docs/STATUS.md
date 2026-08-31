# Status — 1.0.0-beta.2

## Implemented and repaired in this beta

- Correct WinUI 3 resource initialization through merged `XamlControlsResources`.
- Restore the normal generated WinUI application entry point instead of maintaining a duplicate custom Main.
- Add startup diagnostics at `%LocalAppData%\NovaClip\Logs\startup.log` and a visible startup-failure window.
- Replace packaged-only `Windows.Storage.ApplicationData.Current` settings with atomic JSON settings for the unpackaged app.
- Construct the download manager only after settings load so configured startup concurrency is honored.
- Separate media and GitHub update HTTP clients.
- Copy the active Bilibili WebView2 User-Agent and cookies into each in-memory download request and add Bilibili Referer/Origin headers; cookies are not persisted.
- Enable legacy DURL tasks from the UI instead of leaving the button permanently disabled.
- Repair download-state transitions for DURL and single-track finalization and allow failed tasks to retry.
- Add clear preflight handling when DASH merge requires FFmpeg but FFmpeg is unavailable.
- Make private GitHub update access explicit through optional `NOVACLIP_GITHUB_TOKEN` rather than silently returning “no update”.
- Download release assets through the GitHub asset API and verify GitHub SHA-256 digests before execution when present.
- Run installed updates only after NovaClip exits by delegating to `NovaClip.Updater.exe`.
- Fix Browser and History page row definitions.
- Add a Windows CI startup smoke test that launches the actual published `NovaClip.exe` and fails the build if it exits during startup.

## Remaining beta limitations

- FFmpeg is not redistributed in the repository; the tester must provide a compatible `ffmpeg.exe` for DASH audio/video merge.
- The repository is private, so anonymous clients cannot use GitHub Releases for automatic update discovery. A public signed update feed is still required before public distribution.
- Restart recovery can restore non-secret request metadata and Range state, but Bilibili cookies are intentionally not persisted; a task whose signed CDN URL expires after a full app restart may need the media page reopened before retry.
- Real Bilibili playback, login persistence, CDN authorization and FFmpeg output still require a Windows real-device acceptance pass in addition to CI.
