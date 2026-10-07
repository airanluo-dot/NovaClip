# NovaClip 1.0.0-beta.9 candidate

Publication is pending real Windows embedded-browser playback, current-media UI and completed-download acceptance on the final commit. A successful build or homepage WebView smoke is not this evidence.

## Baseline

Rechecked on 2026-10-07: public main and beta.8 tag both point to `b798c571dad1cae03c16129e4b4c43b85b428d7a`; beta.8 is the newest public prerelease. The cloud workspace was initially empty and the clone was clean. The user's installed Windows version and original failing URL remain unverified. The next unused version is beta.9; no existing release assets are replaced.

## Recognition repair

The source confirms a lost-result sequence: a response captures the current generation, same-video identity enrichment increments it, and the response is then discarded; existing ready media can also be cleared. The bridge previously sent only a hydration hint and the browser ignored it, with no connected fallback parser. This source-level finding is distinct from reproducing the user's exact runtime event order.

The candidate preserves generation for same-content enrichment, retires real video/part switches, checks response and wrapper identities, and reconciles bounded early candidates. Actual page playback data and one ordinary permitted API fallback are connected. Document nonce verification/replay and SPA URI observation protect refresh/back/forward and late-message boundaries. Status, available tracks/qualities and the visible descriptor flow into the original download command with current browser request context.

## Three focused optimization rounds

1. UI and frequent events: move bounded response reading/large JSON parsing off WinUI, coalesce authoritative snapshot dispatch, suppress unchanged context/result work, keep quality selection when current media links refresh, reserve a page-data reader so slow network work cannot starve fallback.
2. Concurrency and lifecycle: cancel retired navigation readers and dispose late streams, detach/close the browser on shell close, gate download admission/resume during shutdown, use one shutdown budget and keep cancellation/resources alive through callbacks. DURL now uses real FFmpeg container remux instead of invalid byte concatenation; FFmpeg cancellation waits for process exit and drains output.
3. Architecture and cross-review: centralize content identity and update asset rules, isolate browser observation ownership, remove update business logic's direct static-service/window dependencies. Accept GitHub's actual setup MIME while retaining size/digest validation. Independent review found and corrected wrapper identity loss, late API failure overwrite, new stale-global objects and mixed DASH/DURL selection.

## Verification and limits

An acceptance-only beta.8 overlay actually played `BV17x411w7KC`, reached visible `Ready` for CID `279786`, and offered three video codecs plus one audio track at permitted quality 16. This sample did not reproduce the user’s exact fault. Its initial full-download attempt was blocked by FFmpeg PATH discovery; the comparison harness now uses an explicit ordinary FFmpeg setting for both versions.

Focused ordering, bridge, session, download, update and FFmpeg checks are recorded in the task report. Native FFmpeg checks on Linux exercise actual DASH and single/multiple DURL containers and cancellation. They do not establish Windows playback, Windows throughput, user-account behavior or perceived UI smoothness. No unmeasured improvement percentage is claimed.

The optional Windows acceptance runner records public video identity, advancing playback, card state, track count/quality and completed output hashes, then verifies streams with ffprobe. It distinguishes playback/website blocking from playback observed without media. All existing dependency/localization/architecture/test/packaging gates remain enabled. Publication also requires the real-browser acceptance step; a blocked or failed live check cannot publish. Release artifacts include setup, portable ZIP, SHA256SUMS and commit/version build provenance; the updater still verifies GitHub asset digest and size.
