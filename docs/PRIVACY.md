# Privacy

NovaClip does not operate a telemetry server. Network traffic is limited to Bilibili pages, Bilibili playback/API/CDN requests initiated for the current task, WebView2 runtime traffic and the configured update source.

Login happens inside an application-owned WebView2 profile. NovaClip does not read Chrome or Edge profiles. When a native media download is created, the active Bilibili WebView2 User-Agent and cookies are copied into that task's in-memory request context so the native request matches the authenticated playback session. Cookie values are not written to SQLite, \`settings.json\`, task manifests, resume metadata, startup logs or an extra configuration file.

Task manifests persist only non-secret request metadata needed for recovery, such as Referer, Origin, User-Agent and the observed PlayURL endpoint. Cookie and Authorization values are rejected from durable metadata and redacted from diagnostics.

The update checker requests the configured GitHub Releases endpoint anonymously. A developer may optionally provide a private-repository token through \`NOVACLIP_GITHUB_TOKEN\`; NovaClip does not persist that token. Update packages are checked by the GitHub digest and signed manifest before handoff.
