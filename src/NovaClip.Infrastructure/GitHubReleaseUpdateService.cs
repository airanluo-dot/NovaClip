using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class GitHubReleaseUpdateService : IUpdateService
{
    public const string DefaultRepository = "airanluo-dot/NovaClip";
    private const int MaxReleaseResponseBytes = 4_000_000;
    private readonly HttpClient _httpClient;
    private readonly string _repository;
    private readonly bool _hasAuthentication;

    public GitHubReleaseUpdateService(HttpClient? httpClient = null, string repository = DefaultRepository, string? token = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _repository = IsValidRepository(repository) ? repository : DefaultRepository;
        _hasAuthentication = !string.IsNullOrWhiteSpace(token) && token!.IndexOfAny(['\r', '\n']) < 0;

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NovaClip", "1.0.0-beta.6"));
        }
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (_hasAuthentication)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Trim());
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

        if (response.Content.Headers.ContentLength is > MaxReleaseResponseBytes) throw new InvalidDataException("GitHub release response was unexpectedly large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releaseJson = await ReadBoundedAsync(stream, MaxReleaseResponseBytes, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(releaseJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub release response was not an array.");
        if (!SemanticVersion.TryParse(currentVersion, out var current)) return null;

        AppUpdateInfo? newest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) continue;
            if (release.TryGetProperty("draft", out var draftProperty) && draftProperty.ValueKind == JsonValueKind.True) continue;
            var prerelease = release.TryGetProperty("prerelease", out var preProperty) && preProperty.ValueKind == JsonValueKind.True;
            if (channel == UpdateChannel.Stable && prerelease) continue;
            var tag = release.TryGetProperty("tag_name", out var tagProperty) && tagProperty.ValueKind == JsonValueKind.String ? tagProperty.GetString() : null;
            if (!SemanticVersion.TryParse(tag, out var candidateVersion) || candidateVersion.CompareTo(current) <= 0) continue;
            var assets = release.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array
                ? assetArray.EnumerateArray().Select(ParseAsset).Where(asset => asset is not null).Cast<AppUpdateAsset>().ToArray()
                : [];
            var publishedAt = release.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(published.GetString(), out var parsedPublishedAt) ? parsedPublishedAt : null;
            var candidate = new AppUpdateInfo(
                candidateVersion.ToString(),
                prerelease,
                publishedAt,
                release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null,
                assets);
            if (newest is null || SemanticVersion.TryParse(newest.Version, out var newestVersion) && candidateVersion.CompareTo(newestVersion) > 0) newest = candidate;
        }
        return newest;
    }

    public async Task<string> DownloadAssetAsync(AppUpdateAsset asset, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!Path.IsPathRooted(destinationPath) || string.IsNullOrWhiteSpace(Path.GetFileName(destinationPath))) throw new ArgumentException("The destination path must be an absolute file path.", nameof(destinationPath));
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var assetUri) || !IsTrustedGithubUri(assetUri)) throw new InvalidDataException("Update asset URL is not a trusted GitHub HTTPS URL.");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.download";
        using var request = new HttpRequestMessage(HttpMethod.Get, assetUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
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
                if (total is > 0) progress?.Report(Math.Min(1, (double)received / total.Value));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);

            if (asset.Size is > 0 && received != asset.Size.Value) throw new InvalidDataException("更新包大小校验失败，已拒绝执行。");
            if (!string.IsNullOrWhiteSpace(asset.Digest) && asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var expected = asset.Digest["sha256:".Length..].Trim();
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败，已拒绝执行。");
            }

            File.Move(temporaryPath, destinationPath, true);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }

        progress?.Report(1);
        return destinationPath;
    }

    private static AppUpdateAsset? ParseAsset(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;
        var nameValue = name.GetString();
        var apiUrl = element.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
        var browserUrl = element.TryGetProperty("browser_download_url", out var browser) && browser.ValueKind == JsonValueKind.String ? browser.GetString() : null;
        var urlValue = new[] { apiUrl, browserUrl }.FirstOrDefault(candidate => Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri) && IsTrustedGithubUri(candidateUri));
        var sizeValue = element.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var parsedSize) && parsedSize >= 0 ? parsedSize : null;
        var contentType = element.TryGetProperty("content_type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        var digestValue = element.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String ? digest.GetString() : null;
        return string.IsNullOrWhiteSpace(nameValue) || string.IsNullOrWhiteSpace(urlValue)
            ? null
            : new AppUpdateAsset(
                nameValue,
                urlValue,
                sizeValue,
                contentType,
                digestValue);
    }

    private static bool IsValidRepository(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts.All(IsSafeRepositoryPart);
    }

    private static bool IsSafeRepositoryPart(string value) => value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsTrustedGithubUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length > maximumBytes - read) throw new InvalidDataException("GitHub response exceeded the safety limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }
}
