namespace NovaClip.App;

public sealed class SettingsApplicationCoordinator
{
    private readonly WindowsSettingsStore _settings;
    private readonly NovaClip.Core.IDownloadManager _downloads;
    private readonly object _gate = new();

    public SettingsApplicationCoordinator(WindowsSettingsStore settings, NovaClip.Core.IDownloadManager downloads)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
    }

    public void Apply(Action<WindowsSettingsStore> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            var before = _settings.Capture();
            try
            {
                mutation(_settings);
                _settings.Validate();
                _downloads.SetMaxConcurrentTasks(_settings.MaxConcurrentTasks);
                _settings.Save();
                StartupDiagnostics.Configure(_settings.DebugLogging);
            }
            catch
            {
                _settings.Restore(before);
                StartupDiagnostics.Configure(before.DebugLogging);
                try { _downloads.SetMaxConcurrentTasks(before.MaxConcurrentTasks); } catch { }
                throw;
            }
        }
    }
}
