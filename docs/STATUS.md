# Status — 1.0.0-beta.7 Remediation and Safety Baseline

Development branch: `codex/nova-clip-1.0.0-beta.7-remediation`.

## Completed implementation scope

- Durable task state, atomic manifest/resume writes, SQLite ordered migrations/backups, history keyset pagination and outbox replay.
- Lifecycle-owned DownloadManager with per-run cancellation disposal, graceful drain and output reservation/reclaim.
- DASH and DURL production paths, staging-only FFmpeg merge and final output commit.
- Browser navigation generation, SPA identity propagation, deduplication and single-instance URL activation.
- Strict setup/portable update selection, mandatory GitHub digest, bounded package extraction and journaled rollback.
- Immediate-save settings with schema 4 migration, runtime apply and rollback.
- Bounded structured startup diagnostics, redaction, UI progress coalescing and page subscription cleanup.
- Centrally pinned packages, pinned Actions, stable SDK/language settings, architecture/localization/dependency gates and version-derived packaging.
- The retired BiliNative source tree is excluded from the beta.7 production solution and release path.

## Acceptance evidence

Cross-platform and Windows workflows are defined in `.github/workflows/ci.yml` and `.github/workflows/windows-build.yml`. High-risk tests cover concurrent output names, stale reservations, Range validator changes, DURL/DASH manager completion, SQLite migration/recovery, GitHub asset digest validation, package extraction and updater rollback.

Windows CI remains the release gate for WinUI compilation, resources.pri, real executable startup markers, page construction, portable packaging and Inno Setup output. A public tag/release is valid only after that gate is green.
