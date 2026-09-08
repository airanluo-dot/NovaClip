using System.Collections.Concurrent;
using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class DownloadManagerTests
{
    [Fact]
    public async Task OneHundredSameTitleTasksCommitUniqueOutputs()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "NovaClipTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var engine = new StagingEngine();
        await using var manager = new DownloadManager(engine, maxConcurrentTasks: 3);
        var completed = new ConcurrentDictionary<Guid, DownloadTaskSnapshot>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskChanged += (_, snapshot) =>
        {
            if (snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Cancelled)
            {
                completed[snapshot.Id] = snapshot;
                if (completed.Count == 100) terminal.TrySetResult();
            }
        };

        try
        {
            var media = new MediaDescriptor
            {
                Title = "same title",
                PageUrl = "https://www.bilibili.com/video/BV1TEST",
                Source = ResolverStrategy.PlayUrlResponse,
                Tracks =
                [
                    new MediaTrack
                    {
                        Type = TrackType.Video,
                        TrackId = "video",
                        Size = 1,
                        Urls = [new MediaUrlCandidate("https://cdn.example/video")]
                    }
                ]
            };
            var requests = Enumerable.Range(0, 100).Select(_ => new DownloadRequest(
                Guid.NewGuid(),
                media,
                media.VideoTrack,
                null,
                root,
                "same-title.mp4",
                new RetryPolicy(1),
                MergeAfterDownload: false)).ToArray();

            await Task.WhenAll(requests.Select(request => manager.EnqueueAsync(request)));
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(100, completed.Count);
            Assert.All(completed.Values, snapshot => Assert.Equal(DownloadTaskState.Completed, snapshot.State));
            Assert.Equal(
                100,
                completed.Values.Select(snapshot => snapshot.OutputPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
            Assert.All(
                completed.Values,
                snapshot => Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(snapshot.OutputPath)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class StagingEngine : IDownloadEngine
    {
        public async Task DownloadAsync(
            DownloadRequest request,
            IProgress<DownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            var taskRoot = HttpRangeDownloader.GetTaskRoot(request.OutputDirectory, request.TaskId);
            Directory.CreateDirectory(taskRoot);
            await File.WriteAllBytesAsync(
                Path.Combine(taskRoot, "video.m4s.part"),
                [1],
                cancellationToken);
            progress.Report(new DownloadProgress(
                request.TaskId,
                DownloadTaskState.DownloadingVideo,
                1,
                1));
        }
    }
}
