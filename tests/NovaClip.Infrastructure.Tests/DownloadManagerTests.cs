using System.Collections.Concurrent;
using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class DownloadManagerTests
{
    [Fact]
    public async Task ShutdownWaitsForEnqueueAwaitingReservationAndPausesItsRun()
    {
        var root = CreateRoot();
        using var reservations = new BlockingReservationService();
        var engine = new CancellationEngine();
        await using var manager = new DownloadManager(engine, maxConcurrentTasks: 1, reservations: reservations);
        try
        {
            var enqueue = manager.EnqueueAsync(CreateVideoRequest(root));
            await reservations.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = manager.ShutdownAsync(TimeSpan.FromSeconds(5));
            var finishedBeforeAdmission = shutdown.IsCompleted;

            reservations.Continue.TrySetResult();
            var id = await enqueue.WaitAsync(TimeSpan.FromSeconds(5));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(finishedBeforeAdmission);
            Assert.False(manager.IsAcceptingWork);
            Assert.Equal(DownloadTaskState.Paused, Assert.Single(manager.GetTasks()).State);
            Assert.Equal(id, Assert.Single(manager.GetTasks()).Id);
            Assert.Equal(1, engine.Attempts);
        }
        finally
        {
            reservations.Continue.TrySetResult();
            await manager.ShutdownAsync(TimeSpan.FromSeconds(5));
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ShutdownRejectsResumeWithoutStartingAnotherRun()
    {
        var root = CreateRoot();
        var engine = new CancellationEngine();
        await using var manager = new DownloadManager(engine, maxConcurrentTasks: 1);
        try
        {
            var id = await manager.EnqueueAsync(CreateVideoRequest(root));
            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await manager.PauseAsync(id);
            await manager.ShutdownAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ResumeAsync(id));

            Assert.Equal(1, engine.Attempts);
            Assert.Equal(DownloadTaskState.Paused, Assert.Single(manager.GetTasks()).State);
        }
        finally
        {
            await manager.ShutdownAsync(TimeSpan.FromSeconds(5));
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FailedInitialPersistenceDoesNotLeaveAnUnstartableQueuedTask()
    {
        var root = CreateRoot();
        await using var manager = new DownloadManager(new StagingEngine(), repository: new FailingRepository());
        try
        {
            await Assert.ThrowsAsync<IOException>(() => manager.EnqueueAsync(CreateVideoRequest(root)));

            Assert.Empty(manager.GetTasks());
            Assert.Empty(Directory.GetFiles(root, "*.novaclip-reservation"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ShutdownTimeoutStillStopsTransfersAndDisposalWaitsForPendingAdmission()
    {
        var root = CreateRoot();
        using var reservations = new BlockingReservationService { Block = false };
        var engine = new CancellationEngine();
        var manager = new DownloadManager(engine, maxConcurrentTasks: 1, reservations: reservations);
        try
        {
            await manager.EnqueueAsync(CreateVideoRequest(root));
            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reservations.Block = true;
            var enqueue = manager.EnqueueAsync(CreateVideoRequest(root));
            await reservations.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<TimeoutException>(() => manager.ShutdownAsync(TimeSpan.FromMilliseconds(100)));
            await engine.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.Dispose();
            reservations.Continue.TrySetResult();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => enqueue);
            await manager.DisposeAsync();

            Assert.Equal(1, engine.Attempts);
            Assert.Single(manager.GetTasks());
            Assert.Equal(DownloadTaskState.Paused, manager.GetTasks()[0].State);
        }
        finally
        {
            reservations.Continue.TrySetResult();
            manager.Dispose();
            await manager.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PauseKeepsCancellationSourceAliveUntilAsyncCallbacksFinish()
    {
        var root = CreateRoot();
        using var engine = new HeldCancellationCallbackEngine();
        await using var manager = new DownloadManager(engine);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskChanged += (_, snapshot) =>
        {
            if (snapshot.State == DownloadTaskState.Paused) paused.TrySetResult();
        };
        try
        {
            var id = await manager.EnqueueAsync(CreateVideoRequest(root));
            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pause = manager.PauseAsync(id);
            await engine.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            engine.AllowCallback.Set();
            await pause.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(engine.CallbackCompleted);
        }
        finally
        {
            engine.AllowCallback.Set();
            await manager.ShutdownAsync(TimeSpan.FromSeconds(5));
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FailedRunResumesToCompletionAndIgnoresLateOldProgress()
    {
        var root = CreateRoot();
        var engine = new RecoveryEngine();
        await using var manager = new DownloadManager(engine);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<DownloadTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskChanged += (_, snapshot) =>
        {
            if (snapshot.State == DownloadTaskState.Failed) failed.TrySetResult();
            if (snapshot.State == DownloadTaskState.Completed) completed.TrySetResult(snapshot);
        };
        try
        {
            var id = await manager.EnqueueAsync(CreateVideoRequest(root));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await manager.ResumeAsync(id);
            await engine.SecondRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            engine.FirstProgress!.Report(new DownloadProgress(id, DownloadTaskState.DownloadingVideo, 99, 100));
            Assert.Equal(10, Assert.Single(manager.GetTasks()).DownloadedBytes);
            engine.FinishSecondRun.TrySetResult();
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, engine.Attempts);
            Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(result.OutputPath));
        }
        finally
        {
            engine.FinishSecondRun.TrySetResult();
            await manager.ShutdownAsync(TimeSpan.FromSeconds(5));
            DeleteRoot(root);
        }
    }

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
            await using var manager = new DownloadManager(engine, new SingleConcatFfmpegService(), maxConcurrentTasks: 1);
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

    [Fact]
    public async Task MultipleDurlContainersAreRemuxedInSegmentOrderBeforeCommit()
    {
        var root = CreateRoot();
        var ffmpeg = new ConcatFfmpegService();
        await using var manager = new DownloadManager(new SegmentStagingEngine(), ffmpeg);
        var completed = new TaskCompletionSource<DownloadTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskChanged += (_, snapshot) =>
        {
            if (snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Failed) completed.TrySetResult(snapshot);
        };
        try
        {
            var request = CreateLegacyRequest(root);
            await manager.EnqueueAsync(request);
            var snapshot = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(DownloadTaskState.Completed, snapshot.State);
            Assert.Collection(ffmpeg.InputNames,
                name => Assert.Equal("segment-0000.part", name),
                name => Assert.Equal("segment-0001.part", name));
            Assert.Equal(new byte[] { 4, 2 }, await File.ReadAllBytesAsync(snapshot.OutputPath));
            Assert.False(Directory.Exists(HttpRangeDownloader.GetTaskRoot(root, request.TaskId)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnavailableFfmpegRejectsMultipleDurlBeforeDownloadingOrReservingOutput()
    {
        var root = CreateRoot();
        var engine = new CancellationEngine();
        await using var manager = new DownloadManager(engine);
        try
        {
            var error = await Assert.ThrowsAsync<FfmpegUnavailableException>(() => manager.EnqueueAsync(CreateLegacyRequest(root)));
            Assert.Contains("FFmpeg", error.Message);
            Assert.Equal(0, engine.Attempts);
            Assert.Empty(manager.GetTasks());
            Assert.Empty(Directory.GetFileSystemEntries(root));
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

    private static DownloadRequest CreateVideoRequest(string root)
    {
        var media = new MediaDescriptor
        {
            Title = "lifecycle fixture",
            PageUrl = "https://www.bilibili.com/video/BV1TEST",
            Source = ResolverStrategy.PlayUrlResponse,
            Tracks =
            [new MediaTrack
            {
                Type = TrackType.Video, TrackId = "video", Size = 1,
                Urls = [new MediaUrlCandidate("https://cdn.example/video")]
            }]
        };
        return new DownloadRequest(Guid.NewGuid(), media, media.VideoTrack, null, root,
            "lifecycle-fixture.mp4", new RetryPolicy(1), MergeAfterDownload: false);
    }

    private static DownloadRequest CreateLegacyRequest(string root)
    {
        var media = new MediaDescriptor
        {
            Title = "DURL fixture", PageUrl = "https://www.bilibili.com/video/BV1TEST", Source = ResolverStrategy.PlayUrlResponse,
            LegacySegments =
            [new LegacyMediaSegment(1, [new MediaUrlCandidate("https://cdn.example/segment-1")], 1, 1),
             new LegacyMediaSegment(0, [new MediaUrlCandidate("https://cdn.example/segment-0")], 1, 1)]
        };
        return new DownloadRequest(Guid.NewGuid(), media, null, null, root, "durl-fixture.mp4", new RetryPolicy(1), MergeAfterDownload: false);
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
            await File.WriteAllBytesAsync(Path.Combine(taskRoot, "segment-0000.part"), [8, 9], cancellationToken);
            progress.Report(new DownloadProgress(request.TaskId, DownloadTaskState.DownloadingSegments, 2, 2));
        }
    }

    private sealed class SegmentStagingEngine : IDownloadEngine
    {
        public async Task DownloadAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            var taskRoot = HttpRangeDownloader.GetTaskRoot(request.OutputDirectory, request.TaskId);
            Directory.CreateDirectory(taskRoot);
            foreach (var segment in request.Media.LegacySegments)
                await File.WriteAllBytesAsync(Path.Combine(taskRoot, $"segment-{segment.Index:D4}.part"), [(byte)segment.Index], cancellationToken);
        }
    }

    private sealed class SingleConcatFfmpegService : IFfmpegService
    {
        public Task<FfmpegResult> MergeAsync(string videoPath, string audioPath, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public async Task<FfmpegResult> ConcatenateAsync(IReadOnlyList<string> segmentPaths, string outputPath, CancellationToken cancellationToken)
        {
            var source = Assert.Single(segmentPaths);
            await File.WriteAllBytesAsync(outputPath, await File.ReadAllBytesAsync(source, cancellationToken), cancellationToken);
            return new FfmpegResult(true, 0, outputPath);
        }
    }

    private sealed class ConcatFfmpegService : IFfmpegService
    {
        public IReadOnlyList<string> InputNames { get; private set; } = [];
        public Task<FfmpegResult> MergeAsync(string videoPath, string audioPath, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public async Task<FfmpegResult> ConcatenateAsync(IReadOnlyList<string> segmentPaths, string outputPath, CancellationToken cancellationToken)
        {
            InputNames = segmentPaths.Select(path => Path.GetFileName(path)).ToArray();
            Assert.Equal(new byte[] { 0 }, await File.ReadAllBytesAsync(segmentPaths[0], cancellationToken));
            Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(segmentPaths[1], cancellationToken));
            await File.WriteAllBytesAsync(outputPath, [4, 2], cancellationToken);
            return new FfmpegResult(true, 0, outputPath);
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

    private sealed class CancellationEngine : IDownloadEngine
    {
        public int Attempts;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DownloadAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Stopped.TrySetResult(); }
        }
    }

    private sealed class HeldCancellationCallbackEngine : IDownloadEngine, IDisposable
    {
        private CancellationTokenRegistration _registration;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowCallback { get; } = new();
        public bool CallbackCompleted { get; private set; }
        public async Task DownloadAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            _registration = cancellationToken.Register(() =>
            {
                CallbackEntered.TrySetResult();
                if (!AllowCallback.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Callback was not released");
                CallbackCompleted = cancellationToken.WaitHandle.WaitOne(0);
            });
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public void Dispose()
        {
            _registration.Dispose();
            AllowCallback.Dispose();
        }
    }

    private sealed class RecoveryEngine : IDownloadEngine
    {
        public int Attempts;
        public IProgress<DownloadProgress>? FirstProgress { get; private set; }
        public TaskCompletionSource SecondRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishSecondRun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task DownloadAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Attempts) == 1)
            {
                FirstProgress = progress;
                throw new IOException("Simulated interrupted transfer");
            }
            progress.Report(new DownloadProgress(request.TaskId, DownloadTaskState.DownloadingVideo, 10, 100));
            SecondRunStarted.TrySetResult();
            await FinishSecondRun.Task.WaitAsync(cancellationToken);
            var taskRoot = HttpRangeDownloader.GetTaskRoot(request.OutputDirectory, request.TaskId);
            Directory.CreateDirectory(taskRoot);
            await File.WriteAllBytesAsync(Path.Combine(taskRoot, "video.m4s.part"), [1], cancellationToken);
        }
    }

    private sealed class BlockingReservationService : IOutputReservationService
    {
        private readonly OutputReservationService _inner = new();
        public bool Block { get; set; } = true;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OutputReservation> ReserveAsync(Guid taskId, string directory, string fileName, CancellationToken cancellationToken = default)
        {
            if (Block)
            {
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return await _inner.ReserveAsync(taskId, directory, fileName, cancellationToken);
        }

        public Task<OutputReservation> CommitAsync(OutputReservation reservation, string stagingPath, CancellationToken cancellationToken = default) =>
            _inner.CommitAsync(reservation, stagingPath, cancellationToken);
        public Task ReleaseAsync(OutputReservation reservation, CancellationToken cancellationToken = default) =>
            _inner.ReleaseAsync(reservation, cancellationToken);
        public void Dispose() => _inner.Dispose();
    }

    private sealed class FailingRepository : IDownloadTaskRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Simulated unavailable database"));
        public Task<DownloadTaskSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<DownloadTaskSnapshot?>(null);
        public Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DownloadTaskSnapshot>>([]);
        public Task<DownloadPage> GetPageAsync(int limit, DateTimeOffset? beforeUpdatedAt = null, Guid? beforeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DownloadPage([], null, null, false));
    }
}
