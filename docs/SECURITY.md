# Security boundary

- No DRM key extraction, membership bypass, region bypass, credential capture, cookie scraping from external browsers or brute-force login.
- Only Bilibili-origin page messages are accepted; messages and PlayURL responses have schema/size limits.
- Navigation generation and page identity prevent stale SPA results from reaching the current media card.
- Bilibili cookies copied from NovaClip's own WebView2 session stay in memory and are never persisted to task manifests, SQLite, settings or logs.
- Startup diagnostics use rolling bounded JSON files and redact Cookie, Set-Cookie, SESSDATA, bili_jct, Authorization, token-like query values and full signed media query strings.
- New temporary media files are written under a task-specific `.novaclip` directory. A legacy `.bilinative` directory is only a read-only migration input.
- Output reservations use atomic marker creation, never overwrite a file or directory, and reclaim markers only after the owning process is gone.
- Portable packages enforce Zip Slip, duplicate-entry, reparse/symlink, entry-count, compressed-size, total-size, compression-ratio and disk-space limits.
- Update assets require a safe package name/type, a positive declared size, a GitHub `sha256:` digest, and an embedded-key signed manifest with the expected key ID, version, channel, size and hash.
- The updater waits for the application to exit, journals every backup, preserves user data, and rolls back only paths it can prove were backed up.
- Private-repository authentication is accepted only from the process environment and is not persisted by NovaClip.
