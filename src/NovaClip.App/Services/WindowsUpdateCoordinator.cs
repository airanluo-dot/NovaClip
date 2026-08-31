using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using NovaClip.Core;
using NovaClip.Infrastructure;

namespace NovaClip.App;

public sealed class WindowsUpdateCoordinator
{
    private readonly IUpdateService _updateService;
    private readonly WindowsSettingsStore _settings;

    public WindowsUpdateCoordinator(IUpdateService updateService, WindowsSettingsStore settings)
    {
        _updateService = updateService;
        _settings = settings;
    }

    public AppUpdateInfo? LatestUpdate { get; private set; }
    public event EventHandler<AppUpdateInfo>? UpdateAvailable;

    public async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        LatestUpdate = await _updateService.CheckForUpdateAsync(AppServices.CurrentVersion, _settings.UpdateChannel, cancellationToken).ConfigureAwait(false);
        if (LatestUpdate is not null) UpdateAvailable?.Invoke(this, LatestUpdate);
        return LatestUpdate;
    }

    public async Task CheckSilentlyAsync()
    {
        if (!_settings.AutoCheckUpdates) return;
        try { await CheckAsync().ConfigureAwait(false); }
        catch (Exception exception) { StartupDiagnostics.Warning("Silent update check failed.", exception); }
    }

    public async Task<bool> DownloadAndApplyAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var usePortable = AppServices.IsPortableInstall;
        var asset = usePortable ? update.PortableAsset ?? update.SetupAsset : update.SetupAsset ?? update.PortableAsset;
        if (asset is null) return false;

        var updater = Path.Combine(AppContext.BaseDirectory, "NovaClip.Updater.exe");
        if (!File.Exists(updater)) return false;

        var tempRoot = Path.Combine(Path.GetTempPath(), "NovaClip", update.Version);
        Directory.CreateDirectory(tempRoot);
        var downloadedPath = Path.Combine(tempRoot, asset.Name);
        await _updateService.DownloadAssetAsync(asset, downloadedPath, progress, cancellationToken).ConfigureAwait(false);

        var info = new ProcessStartInfo(updater)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        if (usePortable && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = Path.Combine(tempRoot, "extracted");
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            ZipFile.ExtractToDirectory(downloadedPath, extracted);
            info.ArgumentList.Add("--source");
            info.ArgumentList.Add(extracted);
            info.ArgumentList.Add("--target");
            info.ArgumentList.Add(AppContext.BaseDirectory);
        }
        else
        {
            info.ArgumentList.Add("--installer");
            info.ArgumentList.Add(downloadedPath);
        }

        info.ArgumentList.Add("--restart");
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "NovaClip.exe"));

        if (Process.Start(info) is null) return false;
        App.MainWindow?.Close();
        return true;
    }
}
