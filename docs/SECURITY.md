# Security boundary

- No DRM key extraction, membership bypass, region bypass, credential capture, cookie scraping, or brute-force login.
- Only Bilibili-origin page messages are accepted.
- WebView messages require schema version 1, a known message type and a bounded payload.
- `/playurl` response bodies are capped at 10 MB before parsing.
- Logs must redact `Cookie`, `Set-Cookie`, `SESSDATA`, `bili_jct`, `Authorization` and URL query credentials.
- Temporary media files are written under the selected download directory in a task-specific `.bilinative` folder.
- Update packages are downloaded only from the release asset URL returned by the configured GitHub API. Future public distribution should add SHA-256/signature verification.
