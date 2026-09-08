# NovaClip architecture

## Boundaries

- `NovaClip.Contracts` contains stable capability and transport contracts.
- `NovaClip.Core` contains domain models, state transitions, filenames, retry policy and durable operation contracts; it has no Windows UI dependency.
- `NovaClip.Bilibili` normalizes bounded, schema-versioned page and PlayURL data.
- `NovaClip.Infrastructure` owns streaming HTTP downloads, validator-aware Range resume, output reservations, SQLite migrations/backups, durable outbox replay and update package discovery.
- `NovaClip.Windows` owns Windows-specific policy adapters.
- `NovaClip.App` hosts WinUI 3, WebView2, the persistent browser profile, response observation, settings application and lifecycle composition.
- `NovaClip.Updater` is a small self-contained process that waits for the app, validates the portable package manifest, journals backup/replace operations and restarts the app.

Dependency direction is one-way:

```text
App → Windows → Infrastructure / Bilibili → Core → Contracts
```

## Startup and shutdown ownership

Startup acquires the named instance, initializes bounded diagnostics, migrates settings and SQLite, then creates the service graph and shell. The shell owns one `AppServices` lifetime. Shutdown stops new background update work, drains the download queue with a bounded timeout, flushes durable outbox work and disposes each run's cancellation source before closing the repository and HTTP clients.

Unknown startup exceptions are fatal and reach the startup log/CI smoke gate. Known recoverable media, navigation and update failures remain in their local UI state.

## Media flow

```text
Bilibili page
  → WebView2 document-created bridge reports page context
  → WebResourceResponseReceived observes bounded /playurl JSON
  → MediaDetectionCoordinator assigns page identity + generation
  → PlayUrlNormalizer creates MediaTrack candidates
  → DownloadManager reserves final output and creates a .novaclip task root
  → HttpRangeDownloader streams candidates to durable .part files
  → WindowsFfmpegService writes a verified merge staging file
  → OutputReservationService atomically commits the final output
  → SQLite history/outbox records the result
```

The app never downloads media through JavaScript, Blob URLs or a WASM virtual file system. Progress events may be coalesced for the UI, while task state, resume metadata, operation state and history obligations are durable.

## Browser identity

Every navigation starts a new generation. Page context, push/replace state, popstate, hash changes and observed PlayURL responses carry that identity. Results from an older generation or duplicate fingerprint are discarded before they reach the download card.

## Update flow

```text
GitHub Release API
  → require setup/portable asset, positive size and sha256 digest
  → download signed manifest + signature
  → verify embedded public key + key ID + version/channel + asset hash/size
  → download selected package
  → extract with entry/size/ratio/disk/reparse limits
  → updater waits for app exit
  → journal backup/replace/rollback
  → health-check and restart
```

Installed updates use the setup executable. Portable updates use `portable.marker` and the independent updater. User data is outside the application-owned replacement set and is preserved.
