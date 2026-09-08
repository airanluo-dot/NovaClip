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

    [Fact]
    public async Task DashMergeReentersFinalizingBeforeCommitting()
    {
        var root = CreateRoot();
        try
        {
            var engine = new DashStagingEngine();
            var ffmpeg = new FakeFfmpegService();
            await using var manager = new DownloadManager(engine, ffmpeg, maxConcurrentTasks: 1);
            var completed = new TaskCompletionSource<DownloadTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.TaskChanged += (_, snapshot) =>
            {
                if (snapshot.State == DownloadTaskState.Completed) completed.TrySetResult(snapshot);
            };

            var media = new MediaDescriptor
            {
                Title = "dash fixture",
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
                    },
                    new MediaTrack
                    {
                        Type = TrackType.Audio,
                        TrackId = "audio",
                        Size = 1,
                        Urls = [new MediaUrlCandidate("https://cdn.example/audio")]
                    }
                ]
            };
            var request = new DownloadRequest(
                Guid.NewGuid(),
                media,
                media.VideoTrack,
                media.AudioTrack,
                root,
                "dash-fixture.mp4",
                new RetryPolicy(1),
                MergeAfterDownload: true);

            await manager.EnqueueAsync(request);
            var snapshot = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(ffmpeg.Called);
            Assert.Equal(DurableOperationState.Committed, snapshot.OperationState);
            Assert.Equal(new byte[] { 4, 2 }, await File.ReadAllBytesAsync(snapshot.OutputPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LegacyDurlTaskCommitsThroughDownloadManager()
    {
        var root = CreateRoot();
        try
        {
            var engine = new LegacyStagingEngine();
            await using var manager = new DownloadManager(engine, maxConcurrentTasks: 1);
            var completed = new TaskCompletionSource<DownloadTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.TaskChanged += (_, snapshot) =>
            {
                if (snapshot.State == DownloadTaskState.Completed) completed.TrySetResult(snapshot);
            };
            var media = new MediaDescriptor
            {
                Title = "legacy fixture",
                PageUrl = "https://www.bilibili.com/video/BV1TEST",
                Source = ResolverStrategy.PlayUrlResponse,
                LegacySegments =
                [new LegacyMediaSegment(0, [new MediaUrlCandidate("https://cdn.example/segment")], 2, 1)]
            };
            var request = new DownloadRequest(
                Guid.NewGuid(), media, null, null, root, "legacy-fixture.mp4", new RetryPolicy(1), MergeAfterDownload: false);

            await manager.EnqueueAsync(request);
            var snapshot = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(DownloadTaskState.Completed, snapshot.State);
            Assert.Equal(new byte[] { 8, 9 }, await File.ReadAllBytesAsync(snapshot.OutputPath));
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
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaClipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class DashStagingEngine : IDownloadEngine
    {
        public async Task DownloadAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            var taskRoot = HttpRangeDownloader.GetTaskRoot(request.OutputDirectory, request.TaskId);
            Directory.CreateDirectory(taskRoot);
            await File.WriteAllBytesAsync(Path.Combine(taskRoot, "video.m4s.part"), [1], cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(taskRoot, "audio.m4s.part"), [2], cancellationToken);
            progress.Report(new DownloadProgress(request.TaskId, DownloadTaskState.DownloadingVideo, 2, 2));
        }
    }

    private sealed class FakeFfmpegService : IFfmpegService
    {
        public bool Called { get; private set; }

        public async Task<FfmpegResult> MergeAsync(string videoPath, string audioPath, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Called = true;
            await File.WriteAllBytesAsync(outputPath, [4, 2], cancellationToken);
            return new FfmpegResult(true, 0, outputPath);
        }
    }

    private sealed class LegacyStagingEngine : IDownloadEngine
    {
        public async Task DownloadAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            var taskRoot = HttpRangeDownloader.GetTaskRoot(request.OutputDirectory, request.TaskId);
            Directory.CreateDirectory(taskRoot);
            await File.WriteAllBytesAsync(Path.Combine(taskRoot, "legacy.mp4.part"), [8, 9], cancellationToken);
            progress.Report(new DownloadProgress(request.TaskId, DownloadTaskState.DownloadingSegments, 2, 2));
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
