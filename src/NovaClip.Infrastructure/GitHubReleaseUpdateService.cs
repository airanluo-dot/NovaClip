using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class GitHubReleaseUpdateService : IUpdateService
{
    public const string DefaultRepository = "airanluo-dot/NovaClip";
    private readonly HttpClient _httpClient;
    private readonly string _repository;
    private readonly bool _hasAuthentication;

    public GitHubReleaseUpdateService(HttpClient? httpClient = null, string repository = DefaultRepository, string? token = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _repository = repository;
        _hasAuthentication = !string.IsNullOrWhiteSpace(token);

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NovaClip", "1.0.0-beta.4"));
        }
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (_hasAuthentication)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(string currentVersion, UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases?per_page=20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound && !_hasAuthentication)
        {
            throw new InvalidOperationException("GitHub 更新源不存在或不可访问，请确认仓库地址和访问权限。");
        }
        response.EnsureSuccessStatusCode();

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
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            received += read;
            if (total is > 0) progress?.Report((double)received / total.Value);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(asset.Digest) && asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var expected = asset.Digest["sha256:".Length..].Trim();
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                output.Close();
                File.Delete(destinationPath);
                throw new InvalidDataException("更新包 SHA-256 校验失败，已拒绝执行。");
            }
        }

        progress?.Report(1);
        return destinationPath;
    }

    private static AppUpdateAsset? ParseAsset(JsonElement element)
    {
        if (!element.TryGetProperty("name", out var name)) return null;
        var nameValue = name.GetString();
        var apiUrl = element.TryGetProperty("url", out var url) ? url.GetString() : null;
        var browserUrl = element.TryGetProperty("browser_download_url", out var browser) ? browser.GetString() : null;
        var urlValue = apiUrl ?? browserUrl;
        return string.IsNullOrWhiteSpace(nameValue) || string.IsNullOrWhiteSpace(urlValue)
            ? null
            : new AppUpdateAsset(
                nameValue,
                urlValue,
                element.TryGetProperty("size", out var size) ? size.GetInt64() : null,
                element.TryGetProperty("content_type", out var type) ? type.GetString() : null,
                element.TryGetProperty("digest", out var digest) ? digest.GetString() : null);
    }
}
