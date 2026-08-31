# Reference extension audit

The supplied `bilibili-helper-3.0.4.zip` was audited read-only before implementation.

| Item | Value |
|---|---|
| Archive SHA-256 | `95036016a004107979b179bd4cb43de76e40d95dc2ef9a020f6a3b385f54e1a4` |
| Main script SHA-256 | `89474c3750f92ac9ea2fe5e099c8d8ecccb96d0cc644d989c7c62c959d95963d` |
| Contents | MV3 manifest, seed script, main content script, popup, FFmpeg WASM assets, icon |

Observed behavior used as clean-room behavior notes:

- MV3 injects a seed script at `document_start` on Bilibili hosts. The seed injects the main script into the page world.
- The main script recognizes `/video/av...`, `/video/BV...` and `/bangumi/play/...`.
- Ordinary video page data is read from `window.__INITIAL_STATE__` or `__NEXT_DATA__`.
- Ordinary PlayURL requests use `/x/player/wbi/playurl` with `qn`, `fnval`, `fourk`, `avid`, `bvid` and `cid` parameters.
- DASH is represented by `dash.video[]` and `dash.audio[]`; each track has a primary URL and backup URLs. Legacy responses use `durl[]`.
- Known quality IDs include 16/32/64/80/112/120/125.
- The extension merges separate tracks with FFmpeg WASM using stream copy.

NovaClip uses these facts only as protocol and behavior requirements. It does not copy the extension's JavaScript, UI, remote notice iframe, icon, or FFmpeg WASM. The new bridge is isolated, schema-versioned, and does not replace `XMLHttpRequest` globally.
