# Update design

The first beta deliberately supports two coverage-update mechanisms.

## Installed build

The app checks GitHub Releases for a newer semantic version and downloads the `*-setup.exe` asset. After the app exits, Inno Setup runs silently with the same `AppId` and per-user install directory (`%LocalAppData%\\NovaClip`), replacing the existing application files while leaving AppData settings and WebView2 data untouched.

## Portable build

The portable ZIP contains `portable.marker` and `NovaClip.Updater.exe`. The app downloads the newer `*-portable.zip`, extracts it under `%TEMP%`, starts the updater with the current PID, source and target directories, and exits. The updater waits for the app, copies all new files over the target directory, keeps its own executable in place, and restarts `NovaClip.exe`.

## Safety notes

The beta update source is the GitHub Releases API for `airanluo-dot/NovaClip`. The repository is private during development, so the endpoint is not a public distribution channel. Before public release, add a public signed manifest, asset SHA-256 verification, and a documented key-rotation policy.
