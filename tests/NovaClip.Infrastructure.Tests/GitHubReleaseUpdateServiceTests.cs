using System.Net;
using System.Security.Cryptography;
using System.Text;
using NovaClip.Core;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class GitHubReleaseUpdateServiceTests
{
    [Fact]
    public async Task DownloadsAssetWhenGitHubDigestMatches()
    {
        var root = CreateRoot();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("update-fixture");
            var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            using var client = new HttpClient(new StaticHandler(bytes));
            using var service = new GitHubReleaseUpdateService(client);
            var destination = Path.Combine(root, "NovaClip-win-x64-setup.exe");
            var asset = new AppUpdateAsset(
                "NovaClip-win-x64-setup.exe",
                "https://github.com/airanluo-dot/NovaClip/releases/download/v1.0.0-beta.7/NovaClip-win-x64-setup.exe",
                bytes.Length,
                "application/octet-stream",
                digest);

            await service.DownloadAssetAsync(asset, destination);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RejectsAssetWithoutDigestBeforeNetworkRequest()
    {
        var root = CreateRoot();
        try
        {
            using var handler = new CountingHandler();
            using var client = new HttpClient(handler);
            using var service = new GitHubReleaseUpdateService(client);
            var asset = new AppUpdateAsset(
                "NovaClip-win-x64-setup.exe",
                "https://github.com/airanluo-dot/NovaClip/releases/download/v1.0.0-beta.7/NovaClip-win-x64-setup.exe",
                10,
                "application/octet-stream");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadAssetAsync(asset, Path.Combine(root, asset.Name)));

            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaClipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class StaticHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
