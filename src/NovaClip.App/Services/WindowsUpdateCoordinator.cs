using System.Diagnostics;
using System.Globalization;
using NovaClip.Core;
using NovaClip.Infrastructure;

namespace NovaClip.App;

public sealed class WindowsUpdateCoordinator : IDisposable
{
    private readonly IUpdateService _updateService;
    private readonly WindowsSettingsStore _settings;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public WindowsUpdateCoordinator(IUpdateService updateService, WindowsSettingsStore settings)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public AppUpdateInfo? LatestUpdate { get; private set; }
    public event EventHandler<AppUpdateInfo>? UpdateAvailable;

    public async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        LatestUpdate = await _updateService.CheckForUpdateAsync(AppServices.CurrentVersion, _settings.UpdateChannel, linked.Token).ConfigureAwait(false);
        if (LatestUpdate is not null)
        {
            try { UpdateAvailable?.Invoke(this, LatestUpdate); }
            catch (Exception exception) { StartupDiagnostics.Warning("Update notification failed.", exception); }
        }
        return LatestUpdate;
    }

    public async Task CheckSilentlyAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.AutoCheckUpdates || _lifetime.IsCancellationRequested) return;
        try { await CheckAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { StartupDiagnostics.Warning("Silent update check failed.", exception); }
    }

    public async Task<bool> DownloadAndApplyAsync(
        AppUpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!await _applyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;

        var tempRoot = Path.Combine(Path.GetTempPath(), "NovaClip", "updates", Guid.NewGuid().ToString("N"));
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            var usePortable = AppServices.IsPortableInstall;
            var asset = usePortable ? update.PortableAsset : update.SetupAsset;
            if (asset is null || !IsSafeAssetName(asset.Name)) return false;

            var updater = Path.Combine(AppContext.BaseDirectory, "NovaClip.Updater.exe");
            if (!File.Exists(updater)) return false;

            Directory.CreateDirectory(tempRoot);
            var downloadedPath = Path.Combine(tempRoot, asset.Name);
            await _updateService.DownloadAssetAsync(asset, downloadedPath, progress, linked.Token).ConfigureAwait(false);

            var info = new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            info.ArgumentList.Add("--pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            if (usePortable)
            {
                if (!asset.Name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase)) return false;
                var extracted = Path.Combine(tempRoot, "extracted");
                Directory.CreateDirectory(extracted);
                await PortablePackageExtractor.ExtractAsync(downloadedPath, extracted, linked.Token).ConfigureAwait(false);
                PortablePackageExtractor.ValidateApplicationFiles(extracted);
                info.ArgumentList.Add("--source");
                info.ArgumentList.Add(extracted);
                info.ArgumentList.Add("--target");
                info.ArgumentList.Add(AppContext.BaseDirectory);
            }
            else
            {
                if (!asset.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) return false;
                info.ArgumentList.Add("--installer");
                info.ArgumentList.Add(downloadedPath);
                info.ArgumentList.Add("--target");
                info.ArgumentList.Add(AppContext.BaseDirectory);
            }

            info.ArgumentList.Add("--restart");
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "NovaClip.exe"));

            await AppServices.PrepareForUpdateAsync(linked.Token).ConfigureAwait(true);
            if (Process.Start(info) is null) return false;
            App.MainWindow?.DispatcherQueue.TryEnqueue(() => App.MainWindow.Close());
            return true;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private static bool IsSafeAssetName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name is not "." and not ".." &&
        Path.GetFileName(name) == name &&
        name.IndexOfAny(['/', '\\', '\0']) < 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _applyGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
