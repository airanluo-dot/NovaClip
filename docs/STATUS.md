# Status — beta.9

Baseline main/latest public prerelease: `b798c571dad1cae03c16129e4b4c43b85b428d7a`, `v1.0.0-beta.8` (rechecked 2026-10-07).

The beta.9 change repairs embedded-browser media identity ordering and bounded fallback, UI observation cost, download lifecycle and DURL remux. See [RELEASE_BETA9.md](RELEASE_BETA9.md) for scope and evidence limits.

Candidate `6b494d9` passed [real Windows embedded-browser acceptance](https://github.com/airanluo-dot/NovaClip/actions/runs/37586845978): playback, visible current media, refresh, Home clearing, history traversal and an actual completed HEVC/AAC MP4 download. The acceptance-only beta.8 baseline played the same video but failed Home identity convergence. The final navigation guard adds current NavigationId checks, outgoing-document isolation and canceled-navigation recovery; a separate opt-in sample verifies another video, exact p2 CID and rapid return.

Publication is gated on final-commit Windows build/package checks and real embedded playback -> visible current-media card -> completed download acceptance. The cloud Linux workspace has no KVM/Windows runtime; repository Windows Runner provides actual WinUI/WebView2 evidence. Existing page startup smoke alone does not prove recognition. The user's installed version/original fault URL have not been supplied. CPU/working-set evidence excludes child processes; no measured percentage improvement is established.
