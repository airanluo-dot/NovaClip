# Settings

Settings are typed, validated, versioned and saved atomically to `%LocalAppData%\NovaClip\settings.json`. Runtime/cache, database, browser and logs use separate locations.

- Booleans use ToggleSwitch.
- Concurrency is restricted to RadioButtons 1, 2 or 3.
- Quality, codec, retry, startup, link behavior and update channel use ComboBox/RadioButtons.
- Directories and executables use native pickers.
- Changes save immediately; there is no Save All button.

Schema version **4** migrates earlier beta values by applying defaults for newly introduced fields while preserving valid existing settings. The settings coordinator validates the candidate, applies runtime changes, writes the new document atomically, and rolls the runtime back when persistence or application fails. Invalid or unreadable settings fall back to safe defaults without replacing the last known-good file.
