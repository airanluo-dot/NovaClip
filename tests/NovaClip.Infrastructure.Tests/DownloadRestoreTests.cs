using System.Text.Json;
using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class DownloadRestoreTests
{
    private static readonly string[] FixtureUrls = ["https://cdn.example/video"];
    [Fact]
    public async Task InaccessibleOutputDoesNotPreventOtherTasksRestoring()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaClipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var repository = new SqliteDownloadTaskRepository(Path.Combine(root, "tasks.db"));
            await repository.InitializeAsync();
            var blocked = Guid.NewGuid();
            var healthy = Guid.NewGuid();
            foreach (var id in new[] { blocked, healthy })
            {
                var taskRoot = HttpRangeDownloader.GetTaskRoot(root, id);
                Directory.CreateDirectory(taskRoot);
                await File.WriteAllTextAsync(Path.Combine(taskRoot, "task.json"), JsonSerializer.Serialize(new
                {
                    Title = "restore fixture", PageUrl = "https://www.bilibili.com/video/BV1TEST",
                    MergeAfterDownload = false,
                    Tracks = new[] { new { Type = "Video", TrackId = "video", Size = 1, Urls = FixtureUrls } }
                }));
                await repository.UpsertAsync(new DownloadTaskSnapshot
                {
                    Id = id, Title = "restore fixture", PageUrl = "https://www.bilibili.com/video/BV1TEST",
                    State = DownloadTaskState.Paused, OutputPath = Path.Combine(root, id + ".mp4"),
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            using var reservations = new DeniedReservation(blocked);
            await using var manager = new DownloadManager(new HttpRangeDownloader(new HttpClient()), repository: repository, reservations: reservations);
            await manager.RestoreAsync();
            Assert.Contains(manager.GetTasks(), item => item.Id == healthy && item.State == DownloadTaskState.Paused);
            Assert.NotNull(await repository.GetAsync(blocked)); // Keep the original record recoverable.
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class DeniedReservation(Guid blocked) : IOutputReservationService
    {
        private readonly OutputReservationService _inner = new();
        public Task<OutputReservation> ReserveAsync(Guid id, string directory, string fileName, CancellationToken cancellationToken = default) =>
            id == blocked ? throw new UnauthorizedAccessException("Fixture: output access revoked") : _inner.ReserveAsync(id, directory, fileName, cancellationToken);
        public Task<OutputReservation> CommitAsync(OutputReservation reservation, string stagingPath, CancellationToken cancellationToken = default) => _inner.CommitAsync(reservation, stagingPath, cancellationToken);
        public Task ReleaseAsync(OutputReservation reservation, CancellationToken cancellationToken = default) => _inner.ReleaseAsync(reservation, cancellationToken);
        public void Dispose() => _inner.Dispose();
    }
}
