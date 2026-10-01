using System.Net;
using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class DirectFileDownloadTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task DirectFilePreservesContentsExtensionAndCompletesThroughQueue(int length)
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaClip-direct-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
            using var client = new HttpClient(new FileHandler(bytes));
            await using var manager = new DownloadManager(new HttpRangeDownloader(client), maxConcurrentTasks: 1);
            var terminal = new TaskCompletionSource<DownloadTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.TaskChanged += (_, value) =>
            {
                if (value.State is DownloadTaskState.Completed or DownloadTaskState.Failed) terminal.TrySetResult(value);
            };
            var media = new MediaDescriptor
            {
                Title = "archive.zip", PageUrl = "https://files.example.test/archive.zip", Source = ResolverStrategy.DirectFile,
                Tracks = [new MediaTrack { Type = TrackType.File, TrackId = "file", Urls = [new MediaUrlCandidate("https://files.example.test/archive.zip")] }]
            };
            await manager.EnqueueAsync(new DownloadRequest(Guid.NewGuid(), media, null, null, root, "archive.zip", new RetryPolicy(1), MergeAfterDownload: false));
            var result = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadTaskState.Completed, result.State);
            Assert.EndsWith(".zip", result.OutputPath);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.OutputPath));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FileHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
