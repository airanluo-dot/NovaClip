# API surface

`NovaClip.Contracts` is dependency-free and owns the service boundaries. Major groups are browser/session/navigation/tab/diagnostics, Bilibili context and detection strategies, downloads, FFmpeg/media processing, settings/migrations, localization, updates, and Windows OS adapters.

The beta.7 production path uses the contracts for:

- browser navigation policy and single-instance activation;
- media detection page identity, generation and duplicate suppression;
- download queue, output reservation, durable operation state and history/outbox;
- settings validation, runtime apply and atomic persistence;
- update discovery, package extraction, SHA-256 digest verification and updater handoff.

Future capability contracts remain available for batch/multipart media, subtitles, danmaku, cover art, metadata, audio tracks, playlists, seasons, stream probing, speed limits and scheduling.

Dependency direction:

```text
NovaClip.App → NovaClip.Windows → NovaClip.Infrastructure / NovaClip.Bilibili
             → NovaClip.Core → NovaClip.Contracts
```

Rules are checked by `scripts/check-architecture.ps1`: Contracts cannot reference WinUI, WebView2, SQLite or FFmpeg; page code cannot construct `HttpClient`, write settings files directly or access SQLite; the retired `BiliNative.*` source tree must not return.
