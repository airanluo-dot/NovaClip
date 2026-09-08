# Decisions

## Historical decisions

- Use WinUI 3 + WebView2 for the native Windows shell while retaining Bilibili page compatibility.
- Use WebView2 \`WebResourceResponseReceived\` for bounded \`/playurl\` responses; do not intercept or proxy large media responses.
- Store task metadata in SQLite under LocalAppData. Never store Bilibili cookies.
- Keep video and audio as \`.part\` files until FFmpeg succeeds.
- Ship unpackaged, self-contained x64 output and provide Inno Setup plus a portable updater.

## 1.0.0-beta.7 remediation decisions

- \`version.props\` is the production version/runtime source. The app assembly, manifest, publish script, installer invocation and release asset names derive from it.
- A final output is never selected by existence checks alone: an atomic marker reservation protects concurrent tasks, and stale markers are reclaimed only when their owner is no longer alive.
- Download work has a separate lifecycle owner from the UI. Durable operation state, atomic task/resume metadata and an outbox make restart/replay explicit.
- The old \`BiliNative.*\` source and solution are retired. A legacy \`.bilinative\` task root may be read for migration compatibility, but new work uses \`.novaclip\`.
- Updates require both the GitHub asset \`sha256:\` digest and an independently signed manifest bound to the embedded public key ID \`novaclip-beta7-2026\`.
- Portable update rollback journals each completed backup. An interrupted backup cannot cause the updater to delete an old file that was never backed up.
- SDK, package versions and GitHub Actions references are pinned; dependency policy and NuGet audit run in CI. FFmpeg remains an external, separately licensed Windows dependency.
