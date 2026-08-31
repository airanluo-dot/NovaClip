# Status — 1.0.0-beta.1

## Implemented in this beta

- Solution and project structure for Core, Infrastructure, WebBridge, WinUI App, Updater and tests.
- Clean-room Bilibili page bridge and PlayURL/DASH/DURL normalizer.
- Quality and codec model with unknown-field tolerance.
- Streaming Range downloader with retry, CDN candidate fallback and `.part` files.
- SQLite task/history schema and centralized task state machine.
- WinUI 3 shell with Browser, Downloads, History, Settings and About pages.
- Persistent WebView2 profile and `/playurl` response observation.
- Native FFmpeg `-c copy` process adapter.
- GitHub Release update checking, installer overwrite path and portable updater path.
- Windows CI and tag-based prerelease packaging workflow.

## Known beta limitations

- Real Bilibili playback and WebView2 behavior require Windows and an account with permission to play the media.
- The repository is private, so the default GitHub Release update endpoint requires a permitted client; public distribution needs a public/signed feed.
- FFmpeg is intentionally not bundled until its exact build and license obligations are documented.
- Legacy segmented `durl[]` downloads are supported through sequential segment streaming; richer multi-audio and segment-level UI remain future work.
- GitHub-hosted CI can compile/package Windows artifacts but cannot replace a Windows real-device manual acceptance pass.
