using NovaClip.Infrastructure;
using Xunit;
namespace NovaClip.Infrastructure.Tests;
public sealed class DownloadConnectionBudgetTests
{
    [Fact]
    public async Task GlobalLimitSurvivesConcurrentWaitersAndCancellation()
    {
        var budget = new DownloadConnectionBudget(2);
        using var first = await budget.AcquireAsync(CancellationToken.None);
        using var second = await budget.AcquireAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        var waiting = budget.AcquireAsync(cancelled.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(2, budget.Active);
        first.Dispose(); first.Dispose();
        using var replacement = await budget.AcquireAsync(CancellationToken.None);
        Assert.Equal(2, budget.Active);
    }
    [Fact]
    public async Task LoweringLimitWaitsForExistingTransfersWithoutAbortingThem()
    {
        var budget = new DownloadConnectionBudget(2);
        using var first = await budget.AcquireAsync(CancellationToken.None);
        using var second = await budget.AcquireAsync(CancellationToken.None);
        budget.SetLimit(1);
        var waiting = budget.AcquireAsync(CancellationToken.None).AsTask();
        first.Dispose();
        Assert.False(waiting.IsCompleted);
        second.Dispose();
        using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, budget.Active);
    }
    [Fact]
    public async Task IncreasingLimitWakesWaiters()
    {
        var budget = new DownloadConnectionBudget(1);
        using var first = await budget.AcquireAsync(CancellationToken.None);
        var waiting = budget.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);
        budget.SetLimit(2);
        using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, budget.Active);
    }
    [Fact]
    public async Task StressNeverExceedsSharedCeiling()
    {
        var budget = new DownloadConnectionBudget(7);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(async _ =>
        {
            using var lease = await budget.AcquireAsync(CancellationToken.None);
            Assert.InRange(budget.Active, 1, 7);
            await Task.Yield();
        }));
        Assert.Equal(0, budget.Active);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void RejectsInvalidLimits(int value) => Assert.Throws<ArgumentOutOfRangeException>(() => new DownloadConnectionBudget(value));
}
