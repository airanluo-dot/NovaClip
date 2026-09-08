# Legacy audit

The beta.7 source of truth is the \`NovaClip.*\` tree. The retired \`BiliNative.sln\`, \`src/BiliNative.*\`, and \`tests/BiliNative.*\` trees are removed from the repository and are blocked by the architecture gate.

## Reused behavior

- filename sanitization, semantic versioning, state-machine rules and retry policy;
- DASH/DURL fixtures and candidate URL normalization;
- streaming Range download, SQLite persistence and native FFmpeg process boundaries.

## Deliberately not reused

- the legacy UI composition and code-behind;
- browser navigation and response-observer wiring;
- unbounded or browser-memory media writes;
- the legacy update replacement path;
- reference-extension JavaScript, UI, remote notice iframe or FFmpeg WASM.

## On-disk compatibility

A previously interrupted legacy task may still be discovered under a \`.bilinative\` task root so it can be migrated or safely completed. This is read-only compatibility behavior. Beta.7 creates task state under \`.novaclip\`, and no legacy source project or solution participates in builds, packaging or release automation.
