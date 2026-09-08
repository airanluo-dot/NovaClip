# Third-party notices and license review manifest

NovaClip does not relicense these dependencies. The package's own license and notice files remain authoritative. This checked-in manifest provides coverage for every centrally pinned package; CI fails if a direct central package is omitted.

| Package | Reviewed license / notice | Distribution note |
|---|---|---|
| Microsoft.WindowsAppSDK | MIT | Preserve upstream notices when redistributing the SDK runtime. |
| Microsoft.Web.WebView2 | MIT | WebView2 Evergreen Runtime is an external Microsoft prerequisite. |
| Microsoft.Windows.SDK.BuildTools | MIT | Build-time package; do not imply the SDK is bundled at runtime. |
| Microsoft.Data.Sqlite | MIT | Preserve upstream package notices. |
| SQLitePCLRaw.lib.e_sqlite3 | MIT wrapper; SQLite public-domain core | Preserve both wrapper and SQLite notices where redistributed. |
| xunit | Apache-2.0 | Test-only dependency. |
| xunit.runner.visualstudio | Apache-2.0 | Test-only dependency. |
| Microsoft.NET.Test.Sdk | MIT | Test-only dependency. |
| coverlet.collector | MIT | Test-only dependency. |

Before a tagged release, review the resolved SBOM and any transitive package notices for the exact package versions restored by CI. FFmpeg, if supplied by a user or distributor, is governed by its own LGPL/GPL obligations and is not included here.
