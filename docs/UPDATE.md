# Update design

NovaClip 1.0.0-beta.4 supports installed and portable coverage updates.

## Installed build

NovaClip downloads the matching `*-setup.exe` into a temporary directory, validates the GitHub release SHA-256 digest when GitHub provides one, starts `NovaClip.Updater.exe`, and closes the app. The updater waits until NovaClip has exited before launching Inno Setup silently against the same AppId and per-user install directory. It then restarts NovaClip.

## Portable build

The portable ZIP contains `portable.marker` and `NovaClip.Updater.exe`. NovaClip downloads the newer `*-portable.zip`, verifies the available SHA-256 digest, extracts it under `%TEMP%`, starts the updater with the current PID/source/target paths, and exits. The updater waits, copies files over the target directory, and restarts NovaClip.

## Repository access

The official repository and Releases are public, so normal update checks are anonymous. NovaClip never embeds a GitHub token and never writes one to settings. A developer testing a private fork may expose a read-only token to the process through the `NOVACLIP_GITHUB_TOKEN` environment variable.

## Safety

- Release downloads use the GitHub asset API.
- SHA-256 is checked before execution whenever the Release API supplies a `digest`.
- The running NovaClip process exits before files are replaced.
- A public release should add an independently signed manifest and documented key-rotation policy in addition to GitHub-hosted hashes.
