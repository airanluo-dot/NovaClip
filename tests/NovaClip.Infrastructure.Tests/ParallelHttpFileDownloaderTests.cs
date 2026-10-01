using System.Net;
using System.Net.Http.Headers;
using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class ParallelHttpFileDownloaderTests
{
    [Fact]
    public async Task DownloadsSameFileConcurrentlyWithIdenticalContents()
    {
        using var fixture = new Fixture();
        var size = await fixture.Downloader.TryDownloadAsync(fixture.Uri, fixture.Path, 4, new RetryPolicy(1), null, CancellationToken.None);
        Assert.Equal(fixture.Bytes.LongLength, size);
        Assert.True(fixture.Handler.Peak > 1);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Theory]
    [InlineData("ignore")]
    [InlineData("wrong-range")]
    [InlineData("changed-tag")]
    [InlineData("missing-tag")]
    public async Task UnsafeRangeResponseRequestsSequentialFallback(string mode)
    {
        using var fixture = new Fixture(mode);
        Assert.Null(await fixture.Downloader.TryDownloadAsync(fixture.Uri, fixture.Path, 4, new RetryPolicy(1), null, CancellationToken.None));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task ReusesCompletedPartsOnlyWhenValidatorMatches()
    {
        using var fixture = new Fixture();
        await fixture.Downloader.TryDownloadAsync(fixture.Uri, fixture.Path, 4, new RetryPolicy(1), null, CancellationToken.None);
        File.Delete(fixture.Path);
        var requests = fixture.Handler.PartRequests;
        await fixture.Downloader.TryDownloadAsync(fixture.Uri, fixture.Path, 4, new RetryPolicy(1), null, CancellationToken.None);
        Assert.Equal(requests, fixture.Handler.PartRequests);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Path));
        fixture.Handler.ETag = "\"version-2\"";
        await fixture.Downloader.TryDownloadAsync(fixture.Uri, fixture.Path, 4, new RetryPolicy(1), null, CancellationToken.None);
        Assert.True(fixture.Handler.PartRequests > requests);
    }

    [Fact]
    public async Task ResumesPartialSegmentsWithoutDuplicatingBytes()
    {
        using var fixture = new Fixture();
        await fixture.Downloader.TryDownloadAsync(fixture.Uri, fixture.Path, 4, new RetryPolicy(1), null, CancellationToken.None);
        File.Delete(fixture.Path);
        foreach (var part in Directory.GetFiles(fixture.Path + ".ranges", "part-*.bin"))
        {
            using var stream = new FileStream(part, FileMode.Open, FileAccess.Write);
            stream.SetLength(stream.Length / 2);
        }
        var requests = fixture.Handler.PartRequests;
        await fixture.Downloader.TryDownloadAsync(fixture.Uri, fixture.Path, 4, new RetryPolicy(1), null, CancellationToken.None);
        Assert.Equal(requests + 4, fixture.Handler.PartRequests);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Fact]
    public async Task CancellationCannotProduceCompletedOutput()
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Downloader.TryDownloadAsync(
            fixture.Uri, fixture.Path, 4, new RetryPolicy(1), null, stop.Token));
        Assert.False(File.Exists(fixture.Path));
    }

    private sealed class Fixture : IDisposable
    {
        public byte[] Bytes { get; } = Enumerable.Range(0, 9 * 1024 * 1024 + 7).Select(i => (byte)(i % 251)).ToArray();
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovaClip-range-tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "file.part");
        public Uri Uri { get; } = new("https://download.example.test/file.bin");
        public Handler Handler { get; }
        public HttpClient Client { get; }
        public ParallelHttpFileDownloader Downloader { get; }
        public Fixture(string mode = "normal")
        {
            Directory.CreateDirectory(Root);
            Handler = new Handler(Bytes, mode);
            Client = new HttpClient(Handler);
            Downloader = new ParallelHttpFileDownloader(Client);
        }
        public void Dispose() { Client.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class Handler(byte[] bytes, string mode) : HttpMessageHandler
    {
        private int _active;
        public int Peak;
        public int PartRequests;
        public string ETag = "\"version-1\"";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var from = range.From!.Value;
            var to = range.To!.Value;
            var probe = from == 0 && to == 0;
            if (!probe)
            {
                Interlocked.Increment(ref PartRequests);
                var active = Interlocked.Increment(ref _active);
                int old;
                do { old = Peak; } while (active > old && Interlocked.CompareExchange(ref Peak, active, old) != old);
                try { await Task.Delay(25, cancellationToken); }
                finally { Interlocked.Decrement(ref _active); }
            }
            if (!probe && mode == "ignore") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes, (int)from, (int)(to - from + 1))
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(!probe && mode == "wrong-range" ? from + 1 : from, to, bytes.Length);
            if (mode != "missing-tag") response.Headers.ETag = new EntityTagHeaderValue(!probe && mode == "changed-tag" ? "\"different\"" : ETag);
            return response;
        }
    }
}
