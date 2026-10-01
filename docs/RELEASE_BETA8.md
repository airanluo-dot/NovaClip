# NovaClip 1.0.0-beta.8

- Repair startup/activation lifetime and retain the browser page across navigation.
- Refresh native downloads, history, settings and browser surfaces.
- Add ordinary HTTP(S) file downloads with destination validation and collision-safe filenames.
- Add validated byte-range transfers, persisted partial ranges, source identity checks and sequential fallback.
- Add shared connection limit (default 64, configurable 1–256), aggregate rate limit, adaptive worker ramp-up and per-operation idle deadlines.
- Fix task restore/progress persistence races, duplicate media detection, FFmpeg temporary output format and updater shutdown disposal.
- Preserve application identity and existing installed/portable upgrade modes.

The downloader is an independent implementation. It does not include restricted PCL source, Minecraft-specific installers or arbitrary mirror rewriting. PCL runtime throughput equivalence has not been established. The 256 setting is a global connection ceiling, not a guarantee that every file opens 256 sockets.

Verification: 80 infrastructure tests plus the native Windows build, real page construction and packaging acceptance passed before the final version-only release pass. Publication requires the final version's Windows gate to pass again.
