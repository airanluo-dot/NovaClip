using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class SqliteRepositoryTests
{
    [Fact]
    public async Task KeysetPaginationReturnsStableNonOverlappingPages()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "NovaClipTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "novaclip.db");
        var repository = new SqliteDownloadTaskRepository(database);
        try
        {
            await repository.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var snapshots = Enumerable.Range(0, 5).Select(index => new DownloadTaskSnapshot
            {
                Id = Guid.NewGuid(),
                PageUrl = "https://www.bilibili.com/video/BV1TEST",
                Title = "fixture-" + index,
                State = DownloadTaskState.Completed,
                CreatedAt = now.AddMinutes(index),
                UpdatedAt = now.AddMinutes(index),
                OutputPath = Path.Combine(root, "fixture-" + index + ".mp4")
            }).ToArray();

            foreach (var snapshot in snapshots)
            {
                await repository.UpsertAsync(snapshot);
                await Task.Delay(2);
            }

            var first = await repository.GetPageAsync(2);
            Assert.Equal(2, first.Items.Count);
            Assert.True(first.HasMore);
            Assert.NotNull(first.NextId);
            Assert.NotNull(first.NextUpdatedAt);

            var second = await repository.GetPageAsync(
                2,
                first.NextUpdatedAt,
                first.NextId);
            Assert.Equal(2, second.Items.Count);
            Assert.True(second.HasMore);
            Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));

            var third = await repository.GetPageAsync(
                2,
                second.NextUpdatedAt,
                second.NextId);
            Assert.Single(third.Items);
            Assert.False(third.HasMore);
        }
        finally
        {
            repository.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
