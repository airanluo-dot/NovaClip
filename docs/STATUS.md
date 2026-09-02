# Status — 1.0.0-beta.5 Startup Reliability Fix

Development branch: `refactor/beta.4-native-rebuild`.

## Implemented

- New `NovaClip.Contracts`, `Core`, `Bilibili`, `Infrastructure`, `Windows`, `App`, and `Updater` boundaries.
- XAML-based WinUI 3 shell with Mica, native title bar, NavigationView and native Settings entry.
- Single-window WebView2 navigation policy, complete navigation events, process-failure reporting, SPA generation isolation, and friendly BV/av/ep/ss address input.
- Media detection state machine, fingerprints, deduplication, bounded diagnostics and stale-navigation rejection.
- Typed immediate-save settings with RadioButtons, ComboBox, ToggleSwitch and native file/folder pickers.
- `zh-CN` and `en-US` resources with parity and hard-coded-string CI gates.
- Windows packaging for portable ZIP and Inno Setup installer.

## Release gate

GitHub Actions must compile and test the solution, verify localization and dependency rules, publish `resources.pri` and XBF resources, launch the packaged executable, construct every top-level page, and observe `App.StartupCompleted` plus every `Page.Ready` marker. A beta.5 tag is not created until those checks are green.
