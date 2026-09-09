# Testing

## Cross-platform tests

```bash
dotnet restore NovaClip.slnx
dotnet test NovaClip.slnx -c Release
```

The tests cover filename sanitization, semantic versions, legal task transitions, 200/206 resume behavior, validator changes and total-length changes, backup URL fallback, DASH/DURL normalization, concurrent output reservations, output collision safety, SQLite keyset pagination/migration/recovery, durable download state, bridge schema validation, GitHub asset digest validation, package extraction limits and portable updater rollback.

## Windows CI acceptance

The Windows workflow performs all of these steps before a prerelease is published:

1. Restore and build the complete `NovaClip.slnx` solution for x64.
2. Run all tests, including Windows policy tests.
3. Run dependency, localization and architecture gates.
4. Publish self-contained application and updater output using `version.props`.
5. Verify `resources.pri`, `NovaClip.exe`, package manifest hashes, version and runtime identity.
6. Launch the actual published executable and require `App.StartupCompleted`, `Shell.Ready`, every top-level page marker and `WebView2.Ready`.
7. Build the Inno Setup installer, hash deliverables and upload the ZIP/installer.
8. On the exact beta.7 tag, hash the deliverables, upload the ZIP/installer, and publish the prerelease only after the Windows gate is green.

## Windows real-device acceptance

1. Launch the x64 build on Windows 10 1809+ or Windows 11; the support matrix distinguishes technical target from CI-verified OS.
2. Verify the main window appears; if startup fails, inspect the bounded files under `%LocalAppData%\\NovaClip\\Logs`.
3. Open Bilibili and log in through the embedded browser; restart and verify the application-owned profile retains the session.
4. Open a permitted BV/AV/bangumi page and verify current-page media tracks are detected.
5. Verify native requests inherit the active WebView2 User-Agent, Bilibili Referer/Origin and in-memory cookies.
6. Verify DASH tasks refuse to start with a clear message when merge is enabled but FFmpeg is unavailable.
7. With FFmpeg configured, verify video/audio staging, remux and atomic final output.
8. Verify pause/resume uses HTTP Range, validator changes restart safely and failed tasks can be retried.
9. Verify DURL tasks can be added from the UI and reach Completed.
10. Verify settings persist across restart and invalid settings do not destroy the last good file.
11. Verify installed and portable updates wait for NovaClip to exit and preserve user data on rollback.
