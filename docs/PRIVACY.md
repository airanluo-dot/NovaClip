# Privacy

NovaClip does not operate a telemetry server. Network traffic is limited to Bilibili pages, Bilibili playback/API/CDN requests initiated for the current task, WebView2 runtime traffic, and the configured update source.

Login happens inside an application-owned WebView2 profile. NovaClip does not read Chrome or Edge profiles. Bilibili cookies are copied to an in-memory `CookieContainer` only when the native downloader needs them; they are not written to SQLite, logs, or an extra configuration file.

The update checker sends a normal request for the configured GitHub Releases endpoint. It does not send cookies, account data, media URLs, or downloaded files to NovaClip's author.
