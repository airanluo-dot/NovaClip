using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class DownloaderTests
{
    [Fact]
    public async Task ResumesWith206WithoutAppendingDuplicateBytes()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "video.part");
            await File.WriteAllBytesAsync(path, [1, 2]);
            await WriteResumeMetadataAsync(path, "https://cdn.example/video.m4s", "\"fixture-v1\"", 4);
            var handler = new StaticHandler([1, 2, 3, 4], supportsRange: true);
            var downloader = new HttpRangeDownloader(new HttpClient(handler));
            var track = new MediaTrack
            {
                Type = TrackType.Video,
                TrackId = "video",
                Urls = [new MediaUrlCandidate("https://cdn.example/video.m4s")],
                Size = 4
            };

            var received = await downloader.DownloadTrackAsync(
                track, path, new RetryPolicy(1), null, null, CancellationToken.None);

            Assert.Equal(4, received);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, handler.LastRangeStart);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RejectsResumeWhenResourceValidatorChanges()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "video.part");
            await File.WriteAllBytesAsync(path, [1, 2]);
            await WriteResumeMetadataAsync(path, "https://cdn.example/video.m4s", "\"fixture-v1\"", 4);
            var downloader = new HttpRangeDownloader(new HttpClient(new ChangedValidatorHandler()));
            var track = new MediaTrack
            {
                Type = TrackType.Video,
                TrackId = "video",
                Urls = [new MediaUrlCandidate("https://cdn.example/video.m4s")],
                Size = 4
            };

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                downloader.DownloadTrackAsync(track, path, new RetryPolicy(1), null, null, CancellationToken.None));
            Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FallsBackToBackupUrlAfterServerFailure()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "audio.part");
            var handler = new FallbackHandler();
            var downloader = new HttpRangeDownloader(new HttpClient(handler));
            var track = new MediaTrack
            {
                Type = TrackType.Audio,
                TrackId = "audio",
                Urls =
                [
                    new MediaUrlCandidate("https://cdn.example/bad"),
                    new MediaUrlCandidate("https://cdn.example/good")
                ],
                Size = 3
            };

            await downloader.DownloadTrackAsync(
                track, path, new RetryPolicy(1), null, null, CancellationToken.None);

            Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DownloadsLegacyDurlSegmentsToOwnedStagingFile()
    {
        var root = CreateRoot();
        try
        {
            var output = Path.Combine(root, "legacy.mp4");
            var handler = new LegacyHandler();
            var downloader = new HttpRangeDownloader(new HttpClient(handler));
            var segments = new[]
            {
                new LegacyMediaSegment(
                    0,
                    [new MediaUrlCandidate("https://cdn.example/segment-0")],
                    2,
                    1),
                new LegacyMediaSegment(
                    1,
                    [new MediaUrlCandidate("https://cdn.example/segment-1")],
                    2,
                    1)
            };
            var media = new MediaDescriptor
            {
                Title = "Legacy fixture",
                PageUrl = "https://www.bilibili.com/video/BV1TEST",
                Source = ResolverStrategy.PlayUrlResponse,
                LegacySegments = segments
            };
            var request = new DownloadRequest(
                Guid.NewGuid(),
                media,
                null,
                null,
                root,
                Path.GetFileName(output),
                new RetryPolicy(1));

            await downloader.DownloadAsync(
                request, new Progress<DownloadProgress>(), CancellationToken.None);

            var taskRoot = HttpRangeDownloader.GetTaskRoot(root, request.TaskId);
            Assert.False(File.Exists(output));
            Assert.Equal(
                new byte[] { 1, 2, 3, 4 },
                await File.ReadAllBytesAsync(Path.Combine(taskRoot, "legacy.mp4.part")));
            Assert.True(File.Exists(Path.Combine(taskRoot, "task.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RejectsWrongPartialRangeWithoutOverwritingExistingBytes()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "video.part");
            await File.WriteAllBytesAsync(path, [1, 2]);
            await WriteResumeMetadataAsync(path, "https://cdn.example/video.m4s", "\"fixture-v1\"", 4);
            var downloader = new HttpRangeDownloader(new HttpClient(new WrongRangeHandler()));
            var track = new MediaTrack
            {
                Type = TrackType.Video,
                TrackId = "video",
                Urls = [new MediaUrlCandidate("https://cdn.example/video.m4s")],
                Size = 4
            };

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                downloader.DownloadTrackAsync(track, path, new RetryPolicy(1), null, null, CancellationToken.None));
            Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "NovaClipTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static async Task WriteResumeMetadataAsync(
        string path,
        string url,
        string etag,
        long totalLength)
    {
        var metadata = new
        {
            Url = url,
            CandidateIndex = 0,
            ETag = etag,
            LastModified = (string?)null,
            TotalLength = totalLength
        };
        await File.WriteAllTextAsync(
            HttpRangeDownloader.GetResumeMetadataPath(path),
            JsonSerializer.Serialize(metadata));
    }

    private sealed class StaticHandler(byte[] bytes, bool supportsRange) : HttpMessageHandler
    {
        public long? LastRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRangeStart = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            var offset = supportsRange && LastRangeStart is long start ? (int)start : 0;
            var body = bytes[offset..];
            var response = new HttpResponseMessage(
                offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
            response.Content.Headers.ContentLength = body.Length;
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            if (offset > 0)
            {
                response.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class ChangedValidatorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([9, 10])
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v2\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 3, 4);
            response.Content.Headers.ContentLength = 2;
            return Task.FromResult(response);
        }
    }

    private sealed class FallbackHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri!.AbsoluteUri.EndsWith("bad", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([9, 8, 7])
                });
        }
    }

    private sealed class LegacyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = request.RequestUri!.AbsolutePath.EndsWith(
                "segment-0",
                StringComparison.Ordinal)
                ? new byte[] { 1, 2 }
                : new byte[] { 3, 4 };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
        }
    }

    private sealed class WrongRangeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(0, 3, 4);
            response.Content.Headers.ContentLength = 4;
            return Task.FromResult(response);
        }
    }
}
