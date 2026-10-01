using NovaClip.Infrastructure;
using Xunit;
namespace NovaClip.Infrastructure.Tests;
public sealed class AdaptiveDownloadSchedulerTests
{
    [Fact] public async Task GrowingWorkersRespectLimitAndVisitEveryPartOnce()
    {
        var seen=new int[40];int active=0,peak=0;long progress=0;
        await AdaptiveDownloadScheduler.RunAsync(40,8,async index=>
        {
            Interlocked.Increment(ref seen[index]);
            var current=Interlocked.Increment(ref active);
            int before;do{before=Volatile.Read(ref peak);}while(current>before&&Interlocked.CompareExchange(ref peak,current,before)!=before);
            try { await Task.Delay(45);Interlocked.Add(ref progress,1000); }
            finally { Interlocked.Decrement(ref active); }
        },()=>Interlocked.Read(ref progress),CancellationToken.None);
        Assert.All(seen,value=>Assert.Equal(1,value));Assert.InRange(peak,5,8);Assert.Equal(0,active);
    }
    [Fact] public async Task CancellationObservesAndStopsAllWorkers()
    {
        using var stop=new CancellationTokenSource();int active=0;
        var task=AdaptiveDownloadScheduler.RunAsync(100,64,async _=>
        { Interlocked.Increment(ref active);try { await Task.Delay(Timeout.Infinite,stop.Token); }finally{Interlocked.Decrement(ref active);} },()=>0,stop.Token);
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);Assert.Equal(0,active);
    }
    [Fact] public async Task SingleConnectionNeverOverlaps()
    {
        int active=0,count=0;
        await AdaptiveDownloadScheduler.RunAsync(6,1,async _=>
        { Assert.Equal(1,Interlocked.Increment(ref active));await Task.Yield();Interlocked.Decrement(ref active);Interlocked.Increment(ref count); },()=>count,CancellationToken.None);
        Assert.Equal(6,count);
    }
}
