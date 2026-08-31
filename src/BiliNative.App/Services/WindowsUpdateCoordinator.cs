using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using BiliNative.Core;
using BiliNative.Infrastructure;

namespace BiliNative.App;

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
        try { await CheckAsync().ConfigureAwait(false); } catch { /* Update checks must never block startup. */ }
    }

    public async Task<bool> DownloadAndApplyAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var usePortable = AppServices.IsPortableInstall;
        var asset = usePortable ? update.PortableAsset ?? update.SetupAsset : update.SetupAsset ?? update.PortableAsset;
        if (asset is null) return false;
        var tempRoot = Path.Combine(Path.GetTempPath(), "NovaClip", update.Version);
        Directory.CreateDirectory(tempRoot);
        var downloadedPath = Path.Combine(tempRoot, asset.Name);
        await _updateService.DownloadAssetAsync(asset, downloadedPath, progress, cancellationToken).ConfigureAwait(false);

        if (usePortable && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = Path.Combine(tempRoot, "extracted");
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            ZipFile.ExtractToDirectory(downloadedPath, extracted);
            var updater = Path.Combine(AppContext.BaseDirectory, "NovaClip.Updater.exe");
            if (!File.Exists(updater)) return false;
            var info = new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("--pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--source");
            info.ArgumentList.Add(extracted);
            info.ArgumentList.Add("--target");
            info.ArgumentList.Add(AppContext.BaseDirectory);
            info.ArgumentList.Add("--restart");
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "NovaClip.exe"));
            Process.Start(info);
        }
        else
        {
            var info = new ProcessStartInfo(downloadedPath)
            {
                UseShellExecute = true,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
            };
            Process.Start(info);
        }

        App.MainWindow?.Close();
        return true;
    }
}
