using System.Net.Http.Headers;
using System.Text.Json;
using BiliNative.Core;

namespace BiliNative.Infrastructure;

public sealed class GitHubReleaseUpdateService : IUpdateService
{
    public const string DefaultRepository = "airanluo-dot/NovaClip";
    private readonly HttpClient _httpClient;
    private readonly string _repository;

    public GitHubReleaseUpdateService(HttpClient? httpClient = null, string repository = DefaultRepository)
    {
        _httpClient = httpClient ?? new HttpClient();
        _repository = repository;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NovaClip", "1.0.0-beta.1"));
        }
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(string currentVersion, UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"https://api.github.com/repos/{_repository}/releases?per_page=20", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!SemanticVersion.TryParse(currentVersion, out var current)) return null;

        AppUpdateInfo? newest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var prerelease = release.TryGetProperty("prerelease", out var preProperty) && preProperty.GetBoolean();
            if (channel == UpdateChannel.Stable && prerelease) continue;
            var tag = release.TryGetProperty("tag_name", out var tagProperty) ? tagProperty.GetString() : null;
            if (!SemanticVersion.TryParse(tag, out var candidateVersion) || candidateVersion.CompareTo(current) <= 0) continue;
            var assets = release.TryGetProperty("assets", out var assetArray)
                ? assetArray.EnumerateArray().Select(ParseAsset).Where(asset => asset is not null).Cast<AppUpdateAsset>().ToArray()
                : [];
            var candidate = new AppUpdateInfo(
                candidateVersion.ToString(),
                prerelease,
                release.TryGetProperty("published_at", out var published) && DateTimeOffset.TryParse(published.GetString(), out var publishedAt) ? publishedAt : null,
                release.TryGetProperty("body", out var body) ? body.GetString() : null,
                assets);
            if (newest is null || SemanticVersion.TryParse(newest.Version, out var newestVersion) && candidateVersion.CompareTo(newestVersion) > 0) newest = candidate;
        }
        return newest;
    }

    public async Task<string> DownloadAssetAsync(AppUpdateAsset asset, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        using var response = await _httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        var buffer = new byte[128 * 1024];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (total is > 0) progress?.Report((double)received / total.Value);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        return destinationPath;
    }

    private static AppUpdateAsset? ParseAsset(JsonElement element)
    {
        if (!element.TryGetProperty("name", out var name) || !element.TryGetProperty("browser_download_url", out var url)) return null;
        var nameValue = name.GetString();
        var urlValue = url.GetString();
        return string.IsNullOrWhiteSpace(nameValue) || string.IsNullOrWhiteSpace(urlValue)
            ? null
            : new AppUpdateAsset(nameValue, urlValue, element.TryGetProperty("size", out var size) ? size.GetInt64() : null, element.TryGetProperty("content_type", out var type) ? type.GetString() : null);
    }
}
