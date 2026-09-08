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

    private sealed class RecordingRepository : IDownloadTaskRepository
    {
        public ConcurrentQueue<DownloadTaskSnapshot> Writes { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default)
        {
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
