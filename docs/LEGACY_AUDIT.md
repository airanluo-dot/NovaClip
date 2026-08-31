# Legacy UI audit

## Reusable and retained

- Range downloader, retry executor, download state machine, SQLite repositories.
- DASH/DURL normalization fixtures and parser behavior.
- FFmpeg process execution and update-feed parsing, behind new boundaries.

## Replaced in beta.4

- `BiliNative.App` navigation, pages, service composition, hard-coded UI strings.
- Browser behavior that did not handle new windows, navigation policy, process failure, loading, title, history, or SPA generations.
- Free-form concurrency/retry/path settings and Save All behavior.
- Dynamic C# construction of `MainWindow`, which had been used to bypass XAML/PRI failures.

The `BiliNative.*` tree remains temporarily as read-only migration reference. `NovaClip.slnx`, CI, packaging, and release automation only build the `NovaClip.*` tree.
