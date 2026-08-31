# Localization

User-facing XAML uses `x:Uid`; dynamic text uses `ILocalizationService`. Simplified Chinese and English key sets live under `src/NovaClip.App/Strings` and must remain identical.

`scripts/check-localization.ps1` fails CI for missing keys, literal user-facing XAML properties, or direct `.Text/.Content/.Title` string assignment in page code. Stable English error codes remain separate from localized UI messages.
