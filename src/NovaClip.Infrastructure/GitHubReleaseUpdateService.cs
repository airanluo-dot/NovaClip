using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class GitHubReleaseUpdateService : IUpdateService, IDisposable
{
    public const string DefaultRepository = "airanluo-dot/NovaClip";
    private const int MaxReleaseResponseBytes = 4_000_000;
    private const long MaxUpdateAssetBytes = 4_000_000_000;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _repository;
    private readonly bool _hasAuthentication;
    private int _disposed;

    public GitHubReleaseUpdateService(
        HttpClient? httpClient = null,
        string repository = DefaultRepository,
        string? token = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _repository = IsValidRepository(repository) ? repository : DefaultRepository;
        _hasAuthentication = !string.IsNullOrWhiteSpace(token) && token!.IndexOfAny(['\r', '\n']) < 0;

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NovaClip", "1.0"));
        }
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (_hasAuthentication)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Trim());
        }
    }

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(
        string currentVersion,
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + _repository + "/releases?per_page=20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound && !_hasAuthentication)
        {
            throw new InvalidOperationException("GitHub 更新源不存在或不可访问，请确认仓库地址和访问权限。");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxReleaseResponseBytes)
        {
            throw new InvalidDataException("GitHub release response was unexpectedly large.");
        }

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
            var publishedAt = release.TryGetProperty("published_at", out var published) &&
                published.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(published.GetString(), out var parsedPublishedAt)
                    ? (DateTimeOffset?)parsedPublishedAt
                    : null;
            var candidate = new AppUpdateInfo(
                candidateVersion.ToString(),
                prerelease,
                publishedAt,
                release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null,
                assets);
            if (newest is null ||
                (SemanticVersion.TryParse(newest.Version, out var newestVersion) && candidateVersion.CompareTo(newestVersion) > 0))
            {
                newest = candidate;
            }
        }

        return newest;
    }

    public async Task<string> DownloadAssetAsync(
        AppUpdateAsset asset,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(asset);
        if (!Path.IsPathRooted(destinationPath) || string.IsNullOrWhiteSpace(Path.GetFileName(destinationPath)))
        {
            throw new ArgumentException("The destination path must be an absolute file path.", nameof(destinationPath));
        }

        var expectedDigest = ParseSha256Digest(asset.Digest);
        if (expectedDigest is null) throw new InvalidDataException("更新包缺少有效的 SHA-256 摘要，已拒绝执行。");
        if (asset.Size is not long declaredSize || declaredSize <= 0 || declaredSize > MaxUpdateAssetBytes)
        {
            throw new InvalidDataException("更新包缺少有效的大小声明，已拒绝执行。");
        }

        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var assetUri) || !IsTrustedGithubUri(assetUri))
        {
            throw new InvalidDataException("Update asset URL is not a trusted GitHub HTTPS URL.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".download";
        using var request = new HttpRequestMessage(HttpMethod.Get, assetUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != declaredSize)
            {
                throw new InvalidDataException("更新包大小声明与响应不一致，已拒绝执行。");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (received > declaredSize - read) throw new InvalidDataException("更新包超过声明大小，已拒绝执行。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                progress?.Report(Math.Min(1, (double)received / declaredSize));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var actualDigest = hash.GetHashAndReset();
            if (received != declaredSize || !CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                throw new InvalidDataException("更新包 SHA-256 校验失败，已拒绝执行。");
            }

            await output.DisposeAsync().ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // A failed cleanup is left for the next startup's recovery sweep.
            }
            throw;
        }

        progress?.Report(1);
        return destinationPath;
    }

    public static byte[]? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var normalized = digest.Trim();
        if (!normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = normalized["sha256:".Length..];
        if (hex.Length != 64 || hex.Any(character => !char.IsAsciiHexDigit(character))) return null;
        try { return Convert.FromHexString(hex); } catch (FormatException) { return null; }
    }

    private static AppUpdateAsset? ParseAsset(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var nameValue = name.GetString();
        var apiUrl = element.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
        var browserUrl = element.TryGetProperty("browser_download_url", out var browser) && browser.ValueKind == JsonValueKind.String ? browser.GetString() : null;
        var urlValue = new[] { apiUrl, browserUrl }.FirstOrDefault(candidate =>
            Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri) && IsTrustedGithubUri(candidateUri));
        var sizeValue = element.TryGetProperty("size", out var size) &&
            size.ValueKind == JsonValueKind.Number &&
            size.TryGetInt64(out var parsedSize) &&
            parsedSize > 0 &&
            parsedSize <= MaxUpdateAssetBytes
                ? (long?)parsedSize
                : null;
        var contentType = element.TryGetProperty("content_type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        var digestValue = element.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String ? digest.GetString() : null;
        return string.IsNullOrWhiteSpace(nameValue) || string.IsNullOrWhiteSpace(urlValue) || Path.GetFileName(nameValue) != nameValue || nameValue.IndexOfAny(['/', '\\', '\0']) >= 0
            ? null
            : new AppUpdateAsset(nameValue!, urlValue!, sizeValue, contentType, digestValue);
    }

    private static bool IsValidRepository(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts.All(IsSafeRepositoryPart);
    }

    private static bool IsSafeRepositoryPart(string value) => value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsTrustedGithubUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsHttpClient) _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
