using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using NovaClip.Core;
using NovaClip.Infrastructure;

namespace NovaClip.App;

public sealed class WindowsUpdateCoordinator : IDisposable
{
    private const int MaxSignedManifestCharacters = 1_000_000;
    private const int MaxSignatureCharacters = 16_384;
    private readonly IUpdateService _updateService;
    private readonly WindowsSettingsStore _settings;
    private readonly string _trustedPublicKeyPem;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public WindowsUpdateCoordinator(
        IUpdateService updateService,
        WindowsSettingsStore settings,
        string? trustedPublicKeyPem = null)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _trustedPublicKeyPem = string.IsNullOrWhiteSpace(trustedPublicKeyPem)
            ? SignedUpdateTrustPolicy.PublicKeyPem
            : trustedPublicKeyPem;
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
        var handedOff = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            var usePortable = AppServices.IsPortableInstall;
            var packageType = usePortable ? "portable" : "setup";
            var asset = usePortable ? update.PortableAsset : update.SetupAsset;
            if (asset is null || !IsSafeAssetName(asset.Name)) return false;

            var updater = Path.Combine(AppContext.BaseDirectory, "NovaClip.Updater.exe");
            if (!File.Exists(updater)) return false;

            Directory.CreateDirectory(tempRoot);
            var downloadedPath = Path.Combine(tempRoot, asset.Name);
            await VerifySignedReleaseAsync(update, asset, packageType, tempRoot, progress, linked.Token).ConfigureAwait(false);
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

    private async Task VerifySignedReleaseAsync(
        AppUpdateInfo update,
        AppUpdateAsset selectedAsset,
        string packageType,
        string tempRoot,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var manifestAsset = update.SignedManifestAsset;
        var signatureAsset = update.SignedManifestSignatureAsset;
        if (manifestAsset is null || signatureAsset is null)
        {
            throw new InvalidDataException("更新发布缺少签名清单或签名文件，已拒绝执行。");
        }

        if (manifestAsset.Size is not long manifestSize ||
            manifestSize <= 0 ||
            manifestSize > MaxSignedManifestCharacters ||
            signatureAsset.Size is not long signatureSize ||
            signatureSize <= 0 ||
            signatureSize > MaxSignatureCharacters)
        {
            throw new InvalidDataException("签名清单大小声明无效，已拒绝执行。");
        }

        var manifestPath = Path.Combine(tempRoot, manifestAsset.Name);
        var signaturePath = Path.Combine(tempRoot, signatureAsset.Name);
        await _updateService.DownloadAssetAsync(manifestAsset, manifestPath, progress, cancellationToken).ConfigureAwait(false);
        await _updateService.DownloadAssetAsync(signatureAsset, signaturePath, progress, cancellationToken).ConfigureAwait(false);

        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var signature = await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        if (manifestJson.Length > MaxSignedManifestCharacters || signature.Length > MaxSignatureCharacters ||
            !SignedUpdateManifestVerifier.Verify(manifestJson, signature.Trim(), _trustedPublicKeyPem, out var manifest, out var error))
        {
            throw new InvalidDataException(error ?? "签名清单验证失败，已拒绝执行。");
        }

        var expectedChannel = update.IsPrerelease ? "preview" : "stable";
        if (!SemanticVersion.TryParse(update.Version, out var updateVersion) ||
            !SemanticVersion.TryParse(manifest!.Version, out var manifestVersion) ||
            updateVersion.CompareTo(manifestVersion) != 0 ||
            !string.Equals(manifest.Channel, expectedChannel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("签名清单版本或发布通道不匹配，已拒绝执行。");
        }

        var signedAsset = manifest.Assets.FirstOrDefault(item =>
            string.Equals(item.Name, selectedAsset.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.RuntimeIdentifier, "win-x64", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.PackageType, packageType, StringComparison.OrdinalIgnoreCase));
        var expectedDigest = GitHubReleaseUpdateService.ParseSha256Digest(selectedAsset.Digest);
        if (signedAsset is null ||
            selectedAsset.Size is not long selectedSize ||
            expectedDigest is null ||
            signedAsset.Size != selectedSize ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(signedAsset.Sha256),
                expectedDigest))
        {
            throw new InvalidDataException("签名清单中的更新资产与 GitHub Release 资产不匹配，已拒绝执行。");
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
