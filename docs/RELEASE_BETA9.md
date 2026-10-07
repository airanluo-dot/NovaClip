# NovaClip 1.0.0-beta.9

This prerelease repairs embedded-browser media detection, navigation isolation and download lifecycle. Publication requires real Windows embedded-browser playback, a visible current-media card and a completed download from the same commit as the packages, in addition to the existing build and policy checks.

## Baseline

Rechecked on 2026-10-07: public main and beta.8 tag both point to `b798c571dad1cae03c16129e4b4c43b85b428d7a`; beta.8 is the newest public prerelease. The cloud workspace was initially empty and the clone was clean. The user's installed Windows version and original failing URL remain unverified. The next unused version is beta.9; no existing release assets are replaced.

## Recognition repair

The source confirms a lost-result sequence: a response captures the current generation, same-video identity enrichment increments it, and the response is then discarded; existing ready media can also be cleared. The bridge previously sent only a hydration hint and the browser ignored it, with no connected fallback parser. This source-level finding is distinct from reproducing the user's exact runtime event order.

The repair preserves generation for same-content enrichment, retires real video/part switches, checks response and wrapper identities, and reconciles bounded early candidates. Actual page playback data and one ordinary permitted API fallback are connected. Document nonce verification/replay and native navigation targets protect refresh/back/forward, SPA route changes and late messages. Status, available tracks/qualities and the visible descriptor flow into the original download command with current browser request context.

## Three focused optimization rounds

1. UI and frequent events: move bounded response reading/large JSON parsing off WinUI, coalesce authoritative snapshot dispatch, suppress unchanged context/result work, keep quality selection when current media links refresh, reserve a page-data reader so slow network work cannot starve fallback.
2. Concurrency and lifecycle: cancel retired navigation readers and dispose late streams, detach/close the browser on shell close, gate download admission/resume during shutdown, use one shutdown budget and keep cancellation/resources alive through callbacks. DURL now uses real FFmpeg container remux instead of invalid byte concatenation; FFmpeg cancellation waits for process exit and drains output.
3. Architecture and cross-review: centralize content identity and update asset rules, isolate browser observation ownership, remove update business logic's direct static-service/window dependencies. Accept GitHub's actual setup MIME while retaining size/digest validation. Independent review found and corrected wrapper identity loss, late API failure overwrite, new stale-global objects and mixed DASH/DURL selection.

## Verification and limits

An acceptance-only beta.8 overlay actually played `BV17x411w7KC`, reached visible `Ready` for CID `279786`, and offered three video codecs plus one audio track at permitted quality 16. This sample did not reproduce the user's exact original fault. With the same explicit FFmpeg setting, the baseline passed initial playback and refresh but failed to clear its video identity after navigating Home: an outgoing document's context restored the old video, with no later native source correction. The repair checks both the current DOM source and intended/authoritative page before accepting context.

The beta.9 candidate at `6b494d95233f275686216abb9334503bb3b2a8ba` passed [real Windows acceptance](https://github.com/airanluo-dot/NovaClip/actions/runs/37586845978): advancing embedded playback, matching visible media card, refresh, Home clearing, back/forward/return, and one download through the normal queue. FFprobe found HEVC video and AAC audio in a 199.319433-second, 5,174,631-byte final MP4. The final publication run repeats this gate for its own commit; evidence is attached to that run.

Focused regressions cover both network/context orders, identity enrichment, real video/part switches, late old results, failure recovery, reader cancellation, queue shutdown/admission, persistence, redirect credentials, update selection and FFmpeg parameters. Native FFmpeg checks on Linux exercise actual DASH and single/multiple DURL containers and cancellation. The real Windows sample exercises DASH; Windows DURL and authenticated/VIP/bangumi behavior still need representative manual acceptance under normal content permissions.

The measured candidate run took 42.15 seconds for all playback/navigation/download phases, used 4.16 seconds of WinUI-process CPU and peaked at 179.5 MiB in that process. These exclude WebView2 and FFmpeg child processes. Dispatcher continuation overruns were at most 14.74 ms; this is not UI-thread CPU occupancy. The baseline failed a navigation phase, so total workloads differ and these figures cannot establish a performance improvement. No unmeasured percentage or perceived-smoothness gain is claimed.

Cookie credentials remain in memory and are not persisted with resumable tasks. After restart, an expired or authenticated task may need fresh recognition and a new task; this release does not add automatic authenticated URL renewal. The user's installed version and original failing URL are still unavailable, so the exact user session has not been reproduced.

The Windows acceptance runner records public video identity, advancing playback, card state, track count/quality and completed output hashes, then verifies streams with ffprobe. It distinguishes playback/website blocking from playback observed without media. All existing dependency/localization/architecture/test/packaging gates remain enabled. Publication also requires the real-browser acceptance step; a blocked or failed live check cannot publish. Release artifacts include setup, portable ZIP, SHA256SUMS and commit/version build provenance; the updater still verifies GitHub asset digest and size. Existing release assets are retained, and publication refuses to replace an existing version.

The installer uses the verified clean portable staging payload, excludes portable-only markers, and is silently installed on the Windows Runner to compare every application file hash. Acceptance videos, reports and UI captures are excluded from distributed packages. The complete four-asset release is assembled privately as a draft and published only after identity, commit, size, MIME and digest checks. Setup is initially uploaded as `application/octet-stream`, which beta.8 installed clients already accept; a full installed beta.8 update/apply session has not been exercised.
