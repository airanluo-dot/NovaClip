# Security boundary

- No DRM key extraction, membership bypass, region bypass, credential capture, cookie scraping from external browsers, or brute-force login.
- Only Bilibili-origin page messages are accepted.
- WebView messages require schema version 1, a known message type and a bounded payload.
- `/playurl` response bodies are capped at 10 MB before parsing.
- Bilibili cookies copied from NovaClip's own WebView2 session stay in memory and are never persisted to task manifests, SQLite, settings, or logs.
- Logs must redact `Cookie`, `Set-Cookie`, `SESSDATA`, `bili_jct`, `Authorization` and URL query credentials.
- Temporary media files are written under the selected download directory in a task-specific `.bilinative` folder.
- Update packages are downloaded from the GitHub Release asset API. When GitHub supplies a `sha256:` digest, NovaClip verifies it before executing or extracting the asset.
- Private-repository authentication is accepted only from the process environment and is not persisted by NovaClip.
- Future public distribution should add an independently signed update manifest and a documented key-rotation policy.
