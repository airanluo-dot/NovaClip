using System.Collections.Concurrent;
using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class DownloadPersistenceQueueTests
{
    [Fact]
    public async Task CoalescesProgressUntilCheckpointDueAndDrainsLatestSnapshot()
    {
        var repository = new RecordingRepository();
        await using var worker = new DownloadPersistenceWorker(repository);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var baseline = new DownloadTaskSnapshot
        {
            Id = id,
            PageUrl = "https://www.bilibili.com/video/BV1TEST",
            Title = "checkpoint",
            State = DownloadTaskState.DownloadingVideo,
            OperationState = DurableOperationState.Downloading,
            CreatedAt = now,
            UpdatedAt = now,
            OutputPath = "checkpoint.mp4"
        };

        await worker.EnqueueCriticalAsync(baseline);
        for (var index = 1; index <= 5; index++)
        {
            worker.EnqueueProgress(baseline with
            {
                DownloadedBytes = index * 100_000,
                UpdatedAt = now.AddMilliseconds(index)
            });
        }

        await worker.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, repository.Writes.Count);
        Assert.Equal(500_000, repository.Writes.Last().DownloadedBytes);
    }

    [Fact]
    public async Task DelayedProgressCannotOverwriteCompletedCheckpoint()
    {
        var repository = new RecordingRepository();
        await using var worker = new DownloadPersistenceWorker(repository);
        var now = DateTimeOffset.UtcNow;
        var completed = new DownloadTaskSnapshot
        {
            Id = Guid.NewGuid(), PageUrl = "https://www.bilibili.com/video/BV1TEST",
            Title = "completed", OutputPath = "completed.mp4", RunId = 2,
            State = DownloadTaskState.Completed, OperationState = DurableOperationState.Committed,
            CreatedAt = now, UpdatedAt = now, DownloadedBytes = 2_000_000
        };
        await worker.EnqueueCriticalAsync(completed);
        worker.EnqueueProgress(completed with
        {
            State = DownloadTaskState.DownloadingVideo,
            OperationState = DurableOperationState.Downloading,
            UpdatedAt = now.AddSeconds(-1), DownloadedBytes = 1_000_000
        });
        await worker.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Single(repository.Writes);
        Assert.Equal(DownloadTaskState.Completed, repository.Writes.Last().State);
    }

    [Fact]
    public async Task FailedProgressWritesBackOffAndCanRecover()
    {
        var repository = new RecordingRepository { FailWrites = true };
        await using var worker = new DownloadPersistenceWorker(repository);
        worker.EnqueueProgress(new DownloadTaskSnapshot
        {
            Id = Guid.NewGuid(), PageUrl = "https://www.bilibili.com/video/BV1TEST",
            Title = "retry", OutputPath = "retry.mp4", State = DownloadTaskState.DownloadingVideo,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await repository.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.InRange(Volatile.Read(ref repository.Attempts), 1, 3);
        repository.FailWrites = false;
        await worker.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Single(repository.Writes);
    }

    private sealed class RecordingRepository : IDownloadTaskRepository
    {
        public ConcurrentQueue<DownloadTaskSnapshot> Writes { get; } = new();
        public volatile bool FailWrites;
        public int Attempts;
        public TaskCompletionSource FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            FirstAttempt.TrySetResult();
            if (FailWrites) throw new IOException("Simulated database lock");
            Writes.Enqueue(snapshot);
            return Task.CompletedTask;
        }

        public Task<DownloadTaskSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<DownloadTaskSnapshot?>(null);

        public Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DownloadTaskSnapshot>>([]);

        public Task<DownloadPage> GetPageAsync(
            int limit,
            DateTimeOffset? beforeUpdatedAt = null,
            Guid? beforeId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DownloadPage([], null, null, false));
    }
}
