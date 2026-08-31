# Media detection

The coordinator exposes `Idle`, `WaitingForPageContext`, `Observing`, `CandidateFound`, `Resolving`, `Ready`, `Unsupported`, `PermissionDenied`, `Expired`, and `Error` states.

Every result carries page identity plus quality, codec and navigation generation. Old-generation results and duplicate fingerprints are ignored. Strategies are replaceable through `IMediaDetectionStrategy`; page context, observed PlayURL, hydrate data and authenticated API fallback can be added without changing the page.

Diagnostics retain bounded, non-sensitive state transitions. Cookies, authorization values and full signed media queries are excluded.
