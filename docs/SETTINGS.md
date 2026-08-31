# Settings

Settings are typed, validated, versioned and saved atomically to `%LocalAppData%\NovaClip\settings.json`. Runtime/cache, database, browser and logs use separate locations.

- Booleans use ToggleSwitch.
- Concurrency is restricted to RadioButtons 1, 2 or 3.
- Quality, codec, retry, startup and link behavior use ComboBox.
- Directories and executables use native pickers.
- Changes save immediately; there is no Save All button.

Schema version 2 migrates beta.3 values by applying defaults for newly introduced fields while preserving valid existing settings.
