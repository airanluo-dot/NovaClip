# Status — beta.9 candidate

Baseline main/latest public prerelease: `b798c571dad1cae03c16129e4b4c43b85b428d7a`, `v1.0.0-beta.8` (rechecked 2026-10-07).

The beta.9 development branch repairs embedded-browser media identity ordering and bounded fallback, UI observation cost, download lifecycle and DURL remux. See [RELEASE_BETA9.md](RELEASE_BETA9.md) for scope and evidence limits.

Publication remains gated on final-commit Windows build/package checks and real embedded playback -> visible current-media card -> completed download acceptance. The cloud Linux workspace has no KVM/Windows runtime; repository Windows Runner is being used. Existing page startup smoke does not prove media recognition. The user's installed version/original fault URL have not been supplied.
