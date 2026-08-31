# Testing

## macOS/Linux or CI cross-platform tests

```bash
dotnet test BiliNative.sln -c Release
```

The tests cover filename sanitization, semantic versions, legal task transitions, 200/206 resume behavior, backup URL fallback, DASH/DURL normalization, permission errors and bridge schema validation.

## Windows manual acceptance

1. Start the unpackaged x64 build on Windows 10 1809+ or Windows 11.
2. If WebView2 is missing, verify the UI reports a clear initialization error.
3. Open Bilibili and log in through the embedded browser.
4. Restart NovaClip and verify the application-owned WebView2 profile retains the session.
5. Open a permitted BV video, wait for the current media card, choose a track and add it to the queue.
6. Verify video/audio are written below `.bilinative/<task-id>` without loading the complete media into the UI process.
7. Verify pause leaves `.part` files and resume sends a Range request after resolving fresh URLs.
8. Verify a failed primary CDN candidate can use a backup URL.
9. Verify FFmpeg stream-copy merge produces a playable output file.
10. Close the app with an active task and verify task metadata remains recoverable.
11. Install the setup artifact over an older build and verify it overwrites the same user install directory.
12. Extract the portable artifact, start it, check for a newer release, and verify the updater replaces the app directory and restarts NovaClip.
