# API surface

`NovaClip.Contracts` is dependency-free and owns the service boundaries. Major groups are browser/session/navigation/tab/diagnostics, Bilibili context and detection strategies, downloads, FFmpeg/media processing, settings/migrations, localization, updates, and Windows OS adapters.

Future capability contracts are present for batch/multipart media, subtitles, danmaku, cover art, metadata, audio tracks, playlists, seasons, stream probing, speed limits, and scheduling. They intentionally have no beta.4 implementation.

Dependency direction:

```text
NovaClip.App → NovaClip.Windows → NovaClip.Infrastructure / NovaClip.Bilibili
             → NovaClip.Core → NovaClip.Contracts
```

Rules are checked by `scripts/check-architecture.ps1`: Contracts cannot reference WinUI, WebView2, SQLite, or FFmpeg; the App cannot construct `HttpClient`; page code cannot write settings files directly.
