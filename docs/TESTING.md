# Testing

## Cross-platform tests

```bash
dotnet test NovaClip.slnx -c Release
```

The tests cover filename sanitization, semantic versions, legal task transitions, 200/206 resume behavior, backup URL fallback, DASH/DURL normalization, permission errors and bridge schema validation.

## Windows CI acceptance

The Windows workflow now performs all of these steps before a prerelease is published:

1. Restore and build the full x64 solution.
2. Run the unit tests.
3. Publish the self-contained unpackaged app.
4. Launch the actual published `NovaClip.exe`, wait up to 60 seconds for the startup markers, and construct every top-level page.
5. If it exits, print `%LocalAppData%\NovaClip\Logs\startup.log` and fail the workflow.
6. Build the Inno Setup installer and portable ZIP.
7. Upload artifacts and publish the prerelease only after the build job succeeds.

## Windows real-device acceptance

1. Launch the x64 build on Windows 10 1809+ or Windows 11.
2. Verify the main window appears; if startup fails, inspect `%LocalAppData%\NovaClip\Logs\startup.log`.
3. Open Bilibili and log in through the embedded browser.
4. Restart NovaClip and verify the application-owned WebView2 profile retains the session.
5. Open a permitted BV/AV/bangumi page and verify media tracks are detected.
6. Verify the native request inherits the active WebView2 User-Agent, Bilibili Referer/Origin and in-memory cookies.
7. Verify DASH tasks refuse to start with a clear message when merge is enabled but FFmpeg is unavailable.
8. With FFmpeg configured, verify video/audio are downloaded and remuxed into a playable file.
9. Verify pause/resume uses HTTP Range and failed tasks can be retried.
10. Verify DURL tasks can be added from the UI and reach Completed rather than failing on an illegal state transition.
11. Verify settings persist across restart in `%LocalAppData%\NovaClip\settings.json`.
12. Verify installed and portable updates wait for NovaClip to exit before replacement.
