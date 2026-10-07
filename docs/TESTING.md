# Testing

## Cross-platform tests

```bash
dotnet restore NovaClip.slnx
dotnet test NovaClip.slnx -c Release
```

The tests cover filename sanitization, semantic versions, legal task transitions, 200/206 resume behavior, validator changes and total-length changes, backup URL fallback, DASH/DURL normalization, concurrent output reservations, output collision safety, SQLite keyset pagination/migration/recovery, durable download state, bridge schema validation, GitHub asset digest validation, package extraction limits and portable updater rollback.

## Windows CI acceptance

The Windows workflow performs all of these steps before a prerelease is published:

1. Restore and build the complete `NovaClip.slnx` solution for x64.
2. Run all tests, including Windows policy tests.
3. Run dependency, localization and architecture gates.
4. Publish self-contained application and updater output using `version.props`.
5. Verify `resources.pri`, `NovaClip.exe`, package manifest hashes, version and runtime identity.
6. Launch the actual published executable and require `App.StartupCompleted`, `Shell.Ready`, every top-level page marker and `WebView2.Ready`.
7. For publication, exercise real embedded playback, a matching media card and an actual completed download, then verify its streams with ffprobe.
8. Build the Inno Setup installer, hash the ZIP/installer and write commit/version provenance.
9. On the exact version tag or authorized version publication branch matching main, publish a new prerelease only after all Windows gates pass; refuse replacement of an existing release.

## Windows real-device acceptance

The Windows workflow's optional `media_acceptance_url` dispatch input runs the actual
published WinUI executable against one normal permitted public BV/AV page. It is
separate from construction smoke and uses the application's embedded WebView2,
automatic media detection, visible media card and existing enqueue command.
Playback must advance on the requested video/part before detection is accepted.
The same short sample is refreshed and traversed through Home/back/forward/back;
home must clear the card and each return must converge to the original CID in a
new navigation generation. Only the final visit creates a download task. All
phases share one fixed acceptance deadline.
An optional `media_acceptance_multipart_url` adds one second permitted public
multipart video. The runner observes its p1 playback and card, then reads only
BV/AID and the first two public page CIDs from the current document. The p2
visit must match that exact second CID, differ from p1 and the primary video,
and show advancing playback with a usable card. A rapid primary/p2/primary
return must restore the primary CID before the single final download. BV suffix
comparison remains case sensitive. If the second sample is unavailable or its
page evidence is missing, acceptance reports the gap and does not pass.
The task must reach Completed and FFprobe must find the expected video/audio
streams in the SHA-256-matched final output. Only bounded public identities,
state codes, playback time and process measurements are uploaded; cookies,
media URLs, HTML, and the downloaded content are excluded.

For a local Windows invocation after publishing, close NovaClip first and place
FFmpeg/FFprobe on PATH:

```powershell
./scripts/verify-browser-media.ps1 -ExecutablePath ./publish/win-x64/NovaClip.exe -VideoUrl 'https://www.bilibili.com/video/BV.../' -BuildCommit (git rev-parse HEAD)
```

The PowerShell runner passes the resolved local FFmpeg executable to the normal
in-memory FFmpeg setting for both baseline and candidate runs, then restores the
original settings. The report records whether it was explicitly configured,
without logging the executable path. This avoids treating an unavailable PATH
lookup as an embedded-browser detection failure.

Use a short video you are permitted to download. The runner does not log in,
solve challenges or bypass content permissions. `blocked` means real playback
or a required runtime could not be observed; it is not a successful detection
validation. `PLAYBACK_OBSERVED_MEDIA_NOT_READY` specifically distinguishes a
playing embedded page without a usable current media card. The baseline overlay
input labels acceptance-only instrumentation without changing baseline detection
logic. Record the baseline commit separately from the overlay harness commit.
App CPU and peak working set cover the WinUI process; WebView2 child-process
usage is not included. Dispatcher continuation overruns are an environment
indicator, not a direct measurement of UI thread occupancy or a promised
percentage improvement.

1. Launch the x64 build on Windows 10 1809+ or Windows 11; the support matrix distinguishes technical target from CI-verified OS.
2. Verify the main window appears; if startup fails, inspect the bounded files under `%LocalAppData%\\NovaClip\\Logs`.
3. Open Bilibili and log in through the embedded browser; restart and verify the application-owned profile retains the session.
4. Open a permitted BV/AV/bangumi page and verify current-page media tracks are detected.
5. Verify native requests inherit the active WebView2 User-Agent, Bilibili Referer/Origin and in-memory cookies.
6. Verify DASH tasks refuse to start with a clear message when merge is enabled but FFmpeg is unavailable.
7. With FFmpeg configured, verify video/audio staging, remux and atomic final output.
8. Verify pause/resume uses HTTP Range, validator changes restart safely and failed tasks can be retried.
9. Verify DURL tasks can be added from the UI and reach Completed.
10. Verify settings persist across restart and invalid settings do not destroy the last good file.
11. Verify installed and portable updates wait for NovaClip to exit and preserve user data on rollback.
