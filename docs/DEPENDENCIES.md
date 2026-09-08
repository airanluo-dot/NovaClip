# Dependency and supply-chain policy

## Pinned inputs

- .NET SDK: `global.json` pins 10.0.400 with roll-forward disabled.
- Language/build: `Directory.Build.props` enables nullable, implicit usings, deterministic builds, warnings-as-errors, NuGet audit and restore lock-file generation.
- NuGet: `Directory.Packages.props` centrally pins Windows App SDK, WebView2, Windows SDK Build Tools, Microsoft.Data.Sqlite, xUnit, test SDK, coverlet and SQLitePCLRaw.
- GitHub Actions: workflow action references are immutable 40-character commit SHAs; the dependency policy checks this for every workflow.
- Runtime: beta.7 is `win-x64`; the runtime identifier and product version come from `version.props`.

## Change policy

Dependency updates are reviewed as code changes, run through NuGet audit and the full Core/Infrastructure/Bilibili/Updater/Windows test matrix, then validated by the Windows publish/smoke/package job. Dependabot is configured for monthly NuGet and GitHub Actions update proposals. Preview, floating and project-level package versions are rejected.

## SBOM and licensing

Windows CI emits a CycloneDX SBOM containing the resolved direct and transitive package inventory and uploads it as a build artifact. The dependency policy checks coverage of `docs/THIRD_PARTY_NOTICES.md`; release review must inspect the exact SBOM and transitive notices for the restored versions. The application license is MIT. FFmpeg is not committed; any distributed FFmpeg build must ship its own LGPL/GPL notices and source-obligation information. Bilibili assets and authenticated playback remain subject to their owners' terms.
