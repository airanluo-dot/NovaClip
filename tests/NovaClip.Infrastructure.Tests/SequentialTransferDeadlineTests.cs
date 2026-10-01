using System.Net;
using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class SequentialTransferDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledTransferTimesOutAndReleasesConnection(bool headers)
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaClip-timeout", Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new BlockingHandler(headers));
        var downloader = new HttpRangeDownloader(client, idleTimeout: TimeSpan.FromMilliseconds(80));
        var track = new MediaTrack { Type = TrackType.Video, TrackId = "test", Urls = [new MediaUrlCandidate("https://example.test/file")] };
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadTrackAsync(track,
                Path.Combine(root, "video.part"), new RetryPolicy(1), null, null, watchdog.Token));
            Assert.Equal(0, downloader.Connections.Active);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UserCancellationRemainsCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaClip-timeout", Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new BlockingHandler(true));
        var downloader = new HttpRangeDownloader(client, idleTimeout: TimeSpan.FromSeconds(2));
        var track = new MediaTrack { Type = TrackType.Video, TrackId = "test", Urls = [new MediaUrlCandidate("https://example.test/file")] };
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadTrackAsync(track,
                Path.Combine(root, "video.part"), new RetryPolicy(3), null, null, stop.Token));
            Assert.Equal(0, downloader.Connections.Active);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class BlockingHandler(bool headers) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (headers) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) };
        }
    }
    private sealed class BlockingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    }
}
