namespace NovaClip.App;

public sealed class SettingsApplicationCoordinator
{
    private readonly WindowsSettingsStore _settings;
    private readonly NovaClip.Core.IDownloadManager _downloads;
    private readonly object _gate = new();
    private readonly NovaClip.Infrastructure.DownloadConnectionBudget? _connections;

    public SettingsApplicationCoordinator(WindowsSettingsStore settings, NovaClip.Core.IDownloadManager downloads, NovaClip.Infrastructure.DownloadConnectionBudget? connections = null)
    {
        _connections = connections;
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
                _connections?.SetLimit(_settings.MaxDownloadConnections);
                _settings.Save();
                StartupDiagnostics.Configure(_settings.DebugLogging);
            }
            catch
            {
                _settings.Restore(before);
                _connections?.SetLimit(before.MaxDownloadConnections);
                StartupDiagnostics.Configure(before.DebugLogging);
                try { _downloads.SetMaxConcurrentTasks(before.MaxConcurrentTasks); } catch { }
                throw;
            }
        }
    }
}
