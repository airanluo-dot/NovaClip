using System.Diagnostics;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;
public sealed class DownloadBandwidthLimiterTests
{
    [Fact] public async Task UnlimitedIsImmediate()
    {
        var limiter = new DownloadBandwidthLimiter();
        await limiter.ConsumeAsync(int.MaxValue, CancellationToken.None);
        Assert.Equal(0, limiter.BytesPerSecond);
    }
    [Fact] public async Task ParallelConsumersShareOneBudget()
    {
        var limiter = new DownloadBandwidthLimiter();limiter.SetLimit(100_000);
        var timer=Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0,4).Select(_=>limiter.ConsumeAsync(10_000,CancellationToken.None).AsTask()));
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(230), $"Aggregate limit bypassed: {timer.Elapsed}");
    }
    [Fact] public async Task DisableWakesWaitingConsumers()
    {
        var limiter=new DownloadBandwidthLimiter();limiter.SetLimit(1);
        var pending=limiter.ConsumeAsync(100_000,CancellationToken.None).AsTask();
        Assert.False(pending.IsCompleted);limiter.SetLimit(0);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact] public async Task CancellationDoesNotReserveFutureBandwidth()
    {
        var limiter=new DownloadBandwidthLimiter();limiter.SetLimit(1);
        using var stop=new CancellationTokenSource();var task=limiter.ConsumeAsync(100_000,stop.Token).AsTask();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);
        limiter.SetLimit(100_000);
        await limiter.ConsumeAsync(1,CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }
    [Theory][InlineData(-1)][InlineData(1_073_741_825)]
    public void InvalidLimitIsRejected(long value)=>Assert.Throws<ArgumentOutOfRangeException>(()=>new DownloadBandwidthLimiter().SetLimit(value));
}
