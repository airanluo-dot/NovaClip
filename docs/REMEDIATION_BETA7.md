# beta.7 remediation ledger

- Baseline: `2c38087d8a6f9af55abd7f1dc5f1ae5e11ed96dd`
- Working branch: `codex/nova-clip-1.0.0-beta.7-remediation`
- Target version: `1.0.0-beta.7`
- Production source: `NovaClip.*` solution and `version.props`

| ID range | Closed by beta.7 |
|---|---|
| NC-001–NC-005 | Portable update transaction, atomic reservations, DURL/lifecycle queue path, graceful drain and FFmpeg staging/commit. |
| NC-006–NC-010 | Fatal/recoverable startup policy, page/generation identity, strict update mode selection, validator-aware resume and single-instance URL activation. |
| NC-011–NC-015 | Package extraction budgets, progress coalescing, history keyset pagination, bounded FFmpeg diagnostics and mandatory release digests. |
| NC-016–NC-020 | Signed manifest/key binding, immutable Actions, transactional settings, BrowserPage production wiring and main-branch truth via PR merge gate. |
| NC-021–NC-025 | Legacy BiliNative tree removal, per-run CTS disposal, durable state/outbox, stale-file/user-data update policy and bounded structured diagnostics. |
| NC-026–NC-030 | Support matrix, SDK/language pin, high-risk tests, dependency/audit policy and coordinator production-path coverage. |

## Release gate

All implementation items above are considered complete only when the cross-platform and Windows workflows are green on the same beta.7 head, the packaged executable reaches every startup/page marker, the updater/repository tests pass, and a final second audit finds no stale BiliNative production path, optional digest claim or version mismatch. The signed release job is tag-gated and requires the protected signing secret.
