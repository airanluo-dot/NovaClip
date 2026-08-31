using System.Net;
using System.Net.Http.Headers;
using BiliNative.Core;
using BiliNative.Infrastructure;
using Xunit;

namespace BiliNative.Infrastructure.Tests;

public sealed class DownloaderTests
{
    [Fact]
    public async Task ResumesWith206WithoutAppendingDuplicateBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaClipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "video.part");
            await File.WriteAllBytesAsync(path, [1, 2]);
            var handler = new StaticHandler([1, 2, 3, 4], supportsRange: true);
            var downloader = new HttpRangeDownloader(new HttpClient(handler));
            var track = new MediaTrack { Type = TrackType.Video, TrackId = "video", Urls = [new MediaUrlCandidate("https://cdn.example/video.m4s")], Size = 4 };
            var received = await downloader.DownloadTrackAsync(track, path, new RetryPolicy(1), null, null, CancellationToken.None);
            Assert.Equal(4, received);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, handler.LastRangeStart);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FallsBackToBackupUrlAfterServerFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaClipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "audio.part");
            var handler = new FallbackHandler();
            var downloader = new HttpRangeDownloader(new HttpClient(handler));
            var track = new MediaTrack { Type = TrackType.Audio, TrackId = "audio", Urls = [new MediaUrlCandidate("https://cdn.example/bad"), new MediaUrlCandidate("https://cdn.example/good")], Size = 3 };
            await downloader.DownloadTrackAsync(track, path, new RetryPolicy(1), null, null, CancellationToken.None);
            Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadsLegacyDurlSegmentsToFinalFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaClipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "legacy.mp4");
            var handler = new LegacyHandler();
            var downloader = new HttpRangeDownloader(new HttpClient(handler));
            var segments = new[]
            {
                new LegacyMediaSegment(0, [new MediaUrlCandidate("https://cdn.example/segment-0")], 2, 1),
                new LegacyMediaSegment(1, [new MediaUrlCandidate("https://cdn.example/segment-1")], 2, 1)
            };
            var media = new MediaDescriptor
            {
                Title = "Legacy fixture",
                PageUrl = "https://www.bilibili.com/video/BV1TEST",
                Source = ResolverStrategy.PlayUrlResponse,
                LegacySegments = segments
            };
            var request = new DownloadRequest(Guid.NewGuid(), media, null, null, root, Path.GetFileName(output), new RetryPolicy(1));
            await downloader.DownloadAsync(request, new Progress<DownloadProgress>(), CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(output));
            Assert.True(File.Exists(Path.Combine(root, ".bilinative", request.TaskId.ToString("N"), "task.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class StaticHandler(byte[] bytes, bool supportsRange) : HttpMessageHandler
    {
        public long? LastRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRangeStart = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            var offset = supportsRange && LastRangeStart is long start ? (int)start : 0;
            var body = bytes[offset..];
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
            response.Content.Headers.ContentLength = body.Length;
            if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class FallbackHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri!.AbsoluteUri.EndsWith("bad", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 8, 7]) });
        }
    }

    private sealed class LegacyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.RequestUri!.AbsolutePath.EndsWith("segment-0", StringComparison.Ordinal) ? new byte[] { 1, 2 } : new byte[] { 3, 4 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
