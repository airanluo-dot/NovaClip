# Support matrix

| Environment | Status | Notes |
|---|---|---|
| Windows 10 1809 (10.0.17763) x64 | Technical target | Uses the minimum target framework and x64 unpackaged WinUI path; validate on a real 1809 device before calling it verified. |
| Windows 10 22H2 x64 | Target | Same technical path; WebView2 Evergreen Runtime is required. |
| Windows 11 x64 | CI/primary UX target | Windows CI compiles, publishes and smoke-tests the real executable on the configured runner image. |
| Windows ARM64 | Not supported in beta.7 | No ARM64 package is produced. |
| macOS/Linux | Development only | Cross-platform tests can run where supported; the WinUI/WebView2 application is Windows-only. |
| FFmpeg | Optional for video-only/DURL where applicable; required for DASH remux | User supplies a compatible native executable; it is not bundled in the repository. |

NovaClip does not bypass DRM, membership controls or unavailable playback permissions. WebView2 Evergreen is an external runtime prerequisite and may update independently of NovaClip.
