using NovaClip.App;
using NovaClip.Contracts;
using Xunit;

namespace NovaClip.Bilibili.Tests;

// Compile the real navigation lifetime owner without WebView/WinUI adapters.
public sealed class BrowserMediaDetectionSessionTests
{
    private static readonly Uri FirstVideo = new("https://www.bilibili.com/video/BV1first/");
    private static readonly Uri SecondVideo = new("https://www.bilibili.com/video/BV1second/");

    [Fact]
    public void IdentityEnrichmentKeepsInFlightObservationAlive()
    {
        using var session = new BrowserMediaDetectionSession();
        var generation = session.BeginNavigation(FirstVideo);
        using var lease = session.TryBeginObservation(generation);
        Assert.NotNull(lease);

        var enriched = session.UpdatePageContext(new PageIdentity(
            FirstVideo.ToString(), "BV1first", 7, 101, null, generation, "Current video", PageNumber: 1));

        Assert.Equal(generation, enriched);
        Assert.False(lease.CancellationToken.IsCancellationRequested);
        using var subsequent = session.TryBeginObservation(generation);
        Assert.NotNull(subsequent);
        Assert.Equal(lease.CancellationToken, subsequent.CancellationToken);
    }

    [Fact]
    public void SwitchingVideoCancelsOldReadersWithoutDisposingTheirOwnedSource()
    {
        using var session = new BrowserMediaDetectionSession();
        var first = session.BeginNavigation(FirstVideo);
        var oldLease = session.TryBeginObservation(first);
        Assert.NotNull(oldLease);

        var second = session.BeginNavigation(SecondVideo);

        Assert.True(oldLease.CancellationToken.IsCancellationRequested);
        Assert.Null(session.TryBeginObservation(first));
        using var currentLease = session.TryBeginObservation(second);
        Assert.NotNull(currentLease);
        Assert.False(currentLease.CancellationToken.IsCancellationRequested);

        oldLease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => oldLease.CancellationToken);
    }

    [Fact]
    public async Task CancelledReadersReleaseBoundedCapacityForCurrentVideo()
    {
        using var session = new BrowserMediaDetectionSession();
        var first = session.BeginNavigation(FirstVideo);
        var readers = Enumerable.Range(0, 3).Select(_ => session.TryBeginObservation(first))
            .Append(session.TryBeginObservation(first, isPageData: true)).ToArray();
        Assert.All(readers, reader => Assert.NotNull(reader));
        Assert.Null(session.TryBeginObservation(first));
        Assert.Null(session.TryBeginObservation(first, isPageData: true));
        var blockedReads = readers.Select(async reader =>
        {
            using (reader)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await Task.Delay(Timeout.InfiniteTimeSpan, reader!.CancellationToken));
            }
        }).ToArray();

        var second = session.BeginNavigation(SecondVideo);
        await Task.WhenAll(blockedReads).WaitAsync(TimeSpan.FromSeconds(5));

        using var current = session.TryBeginObservation(second);
        Assert.NotNull(current);
        Assert.False(current.CancellationToken.IsCancellationRequested);
        using var fallback = session.TryBeginObservation(second, isPageData: true);
        Assert.NotNull(fallback);
    }

    [Fact]
    public void SlowNetworkReadsLeaveOneBoundedSlotForPageEvidence()
    {
        using var session = new BrowserMediaDetectionSession();
        var generation = session.BeginNavigation(FirstVideo);
        using var first = session.TryBeginObservation(generation);
        using var second = session.TryBeginObservation(generation);
        using var third = session.TryBeginObservation(generation);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Null(session.TryBeginObservation(generation));

        using var pageEvidence = session.TryBeginObservation(generation, isPageData: true);

        Assert.NotNull(pageEvidence);
        Assert.Null(session.TryBeginObservation(generation, isPageData: true));
        Assert.Null(session.TryBeginObservation(generation));
    }

    [Fact]
    public void CancellationCallbackCanReleaseLeaseBeforeCancellationReturns()
    {
        using var session = new BrowserMediaDetectionSession();
        var first = session.BeginNavigation(FirstVideo);
        var lease = session.TryBeginObservation(first);
        Assert.NotNull(lease);
        var sourceStayedAliveInCallback = false;
        using var callback = lease.CancellationToken.Register(() =>
        {
            lease.Dispose();
            // The source must survive the rest of this Cancel callback even when
            // its final reader releases ownership from inside the callback.
            sourceStayedAliveInCallback = lease.CancellationToken.IsCancellationRequested;
        });

        var second = session.BeginNavigation(SecondVideo);

        Assert.True(sourceStayedAliveInCallback);
        Assert.Throws<ObjectDisposedException>(() => lease.CancellationToken);
        using var current = session.TryBeginObservation(second);
        Assert.NotNull(current);
    }

    [Fact]
    public void ResetCancelsOldWorkAndAllowsFreshNavigation()
    {
        using var session = new BrowserMediaDetectionSession();
        var first = session.BeginNavigation(FirstVideo);
        using var oldLease = session.TryBeginObservation(first);
        Assert.NotNull(oldLease);

        session.Reset();

        Assert.True(oldLease.CancellationToken.IsCancellationRequested);
        Assert.Equal(MediaDetectionState.Idle, session.Detector.Snapshot.State);
        Assert.Null(session.TryBeginObservation(first));
        var currentGeneration = session.BeginNavigation(FirstVideo);
        using var current = session.TryBeginObservation(currentGeneration);
        Assert.NotNull(current);
        Assert.True(currentGeneration > first);
    }

    [Fact]
    public void CloseCancelsReadersAndRejectsFurtherAdmission()
    {
        var session = new BrowserMediaDetectionSession();
        var generation = session.BeginNavigation(FirstVideo);
        var lease = session.TryBeginObservation(generation);
        Assert.NotNull(lease);

        session.Dispose();

        Assert.True(lease.CancellationToken.IsCancellationRequested);
        Assert.Null(session.TryBeginObservation(generation));
        lease.Dispose();
        lease.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.CancellationToken);
    }
}
