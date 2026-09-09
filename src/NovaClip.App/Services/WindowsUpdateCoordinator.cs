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

    public WindowsUpdateCoordinator(
        IUpdateService updateService,
        WindowsSettingsStore settings)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public AppUpdateInfo? LatestUpdate { get; private set; }
    public event EventHandler<AppUpdateInfo>? UpdateAvailable;

    public void Stop()
    {
        _lifetime.Cancel();
    }

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
        var handedOff = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            var usePortable = AppServices.IsPortableInstall;
            var packageType = usePortable ? "portable" : "setup";
            var asset = usePortable ? update.PortableAsset : update.SetupAsset;
            if (asset is null || !IsExpectedPackageAsset(asset, packageType)) return false;

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
                info.ArgumentList.Add("--installer");
                info.ArgumentList.Add(downloadedPath);
                info.ArgumentList.Add("--target");
                info.ArgumentList.Add(AppContext.BaseDirectory);
            }

            info.ArgumentList.Add("--restart");
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "NovaClip.exe"));

            await AppServices.PrepareForUpdateAsync(linked.Token).ConfigureAwait(true);
            if (Process.Start(info) is null) return false;
            handedOff = true;
            App.MainWindow?.DispatcherQueue.TryEnqueue(() => App.MainWindow.Close());
            StartupDiagnostics.Info($"Update handoff completed: version={update.Version}, package={packageType}.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning($"Update rejected or failed before handoff: version={update.Version}.", exception);
            return false;
        }
        finally
        {
            if (!handedOff) TryDeleteDirectory(tempRoot);
            _applyGate.Release();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // The temporary update directory is safe to retry on the next cleanup sweep.
        }
        catch (UnauthorizedAccessException)
        {
            // The temporary update directory is safe to retry on the next cleanup sweep.
        }
    }

    private static bool IsExpectedPackageAsset(AppUpdateAsset asset, string packageType)
    {
        if (!IsSafeAssetName(asset.Name)) return false;
        var expectedSuffix = packageType == "portable" ? "-portable.zip" : "-setup.exe";
        if (!asset.Name.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(asset.ContentType)) return true;

        var contentType = asset.ContentType.Trim();
        return packageType == "portable"
            ? contentType.Equals("application/zip", StringComparison.OrdinalIgnoreCase) ||
              contentType.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase) ||
              contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            : contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
              contentType.Equals("application/x-msdownload", StringComparison.OrdinalIgnoreCase) ||
              contentType.Equals("application/vnd.microsoft.portable-executable", StringComparison.OrdinalIgnoreCase);
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
