using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class OutputReservationTests
{
    [Fact]
    public async Task ConcurrentReservationsProduceUniquePaths()
    {
        var root = CreateRoot();
        using var service = new OutputReservationService();
        try
        {
            var reservations = await Task.WhenAll(
                Enumerable.Range(0, 100).Select(_ =>
                    service.ReserveAsync(Guid.NewGuid(), root, "same-title.mp4")));

            Assert.Equal(100, reservations.Select(item => item.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(reservations, item => Assert.True(File.Exists(item.MarkerPath)));

            foreach (var reservation in reservations)
            {
                await service.ReleaseAsync(reservation);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CommitNeverOverwritesAnExistingOutput()
    {
        var root = CreateRoot();
        using var service = new OutputReservationService();
        try
        {
            var existing = Path.Combine(root, "same-title.mp4");
            await File.WriteAllBytesAsync(existing, [7, 7]);
            var reservation = await service.ReserveAsync(Guid.NewGuid(), root, "same-title.mp4");
            var staging = Path.Combine(root, "staging.tmp");
            await File.WriteAllBytesAsync(staging, [1, 2, 3]);

            var committed = await service.CommitAsync(reservation, staging);

            Assert.NotEqual(existing, committed.OutputPath);
            Assert.Equal(new byte[] { 7, 7 }, await File.ReadAllBytesAsync(existing));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(committed.OutputPath));
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
}
