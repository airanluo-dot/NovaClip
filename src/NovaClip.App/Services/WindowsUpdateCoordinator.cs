using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using NovaClip.Core;
using NovaClip.Infrastructure;

namespace NovaClip.App;

public sealed class WindowsUpdateCoordinator : IDisposable
{
    private readonly IUpdateService _updateService;
    private readonly WindowsSettingsStore _settings;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

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
        if (LatestUpdate is not null)
        {
            try { UpdateAvailable?.Invoke(this, LatestUpdate); }
            catch (Exception exception) { StartupDiagnostics.Warning("Update notification failed.", exception); }
        }
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
        ArgumentNullException.ThrowIfNull(update);
        if (!await _applyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            var usePortable = AppServices.IsPortableInstall;
            var asset = usePortable ? update.PortableAsset ?? update.SetupAsset : update.SetupAsset ?? update.PortableAsset;
            if (asset is null) return false;
            if (!IsSafeAssetName(asset.Name)) return false;

            var updater = Path.Combine(AppContext.BaseDirectory, "NovaClip.Updater.exe");
            if (!File.Exists(updater)) return false;

            var tempRoot = Path.Combine(Path.GetTempPath(), "NovaClip", "updates", Guid.NewGuid().ToString("N"));
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
                ExtractPortablePackage(downloadedPath, extracted);
                if (!File.Exists(Path.Combine(extracted, "NovaClip.exe")) || !File.Exists(Path.Combine(extracted, "NovaClip.Updater.exe"))) return false;
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

            if (Process.Start(info) is null) return false;
            if (App.MainWindow is { } window) window.DispatcherQueue.TryEnqueue(() => window.Close());
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

    private static void ExtractPortablePackage(string archivePath, string destination)
    {
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName)) continue;
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("The update archive contains a symbolic link.");
            var normalizedName = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(destination, normalizedName));
            if (!fullPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update archive contains an unsafe path.");
        }
        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(archivePath, destination);
    }

    public void Dispose()
    {
        _applyGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
