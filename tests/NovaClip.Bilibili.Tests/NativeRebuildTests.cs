using NovaClip.Contracts;
using Xunit;

namespace NovaClip.Bilibili.Tests;

public sealed class NativeRebuildTests
{
    [Theory]
    [InlineData("BV1ab411c7mD", "https://www.bilibili.com/video/BV1ab411c7mD")]
    [InlineData("av170001", "https://www.bilibili.com/video/av170001")]
    [InlineData("ep123", "https://www.bilibili.com/bangumi/play/ep123")]
    public void ResolvesFriendlyInput(string input, string expected)
    {
        var resolver = new BilibiliUrlResolver();
        Assert.True(resolver.TryResolve(input, out var uri));
        Assert.Equal(expected, uri.ToString().TrimEnd('/'));
    }

    [Fact]
    public async Task IgnoresResultsFromOldNavigationGeneration()
    {
        var strategy = new BlockingStrategy();
        var coordinator = new MediaDetectionCoordinator([strategy]);
        coordinator.BeginNavigation(new Uri("https://www.bilibili.com/video/BV1ab411c7mD"));
        var detect = coordinator.DetectAsync();
        coordinator.BeginNavigation(new Uri("https://www.bilibili.com/video/BV1xx411c7mD"));
        strategy.Complete();
        await detect;
        Assert.NotEqual(MediaDetectionState.Ready, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task StrategyFailureBecomesRecoverableCoordinatorError()
    {
        var coordinator = new MediaDetectionCoordinator([new ThrowingStrategy()]);
        coordinator.BeginNavigation(new Uri("https://www.bilibili.com/video/BV1ab411c7mD"));
        await coordinator.DetectAsync();
        Assert.Equal(MediaDetectionState.Error, coordinator.Snapshot.State);
        Assert.Equal("MEDIA_STRATEGY_FAILED", coordinator.Snapshot.ErrorCode);
    }

    [Fact]
    public void ResolverAcceptsBareB23Host()
    {
        Assert.True(new BilibiliUrlResolver().TryResolve("b23.tv/abc", out var uri));
        Assert.Equal("https://b23.tv/abc", uri.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedDetectionKeepsPreviouslyRecognizedMediaReady(bool viaObservation)
    {
        var strategy = new BlockingStrategy();
        strategy.Complete();
        var coordinator = new MediaDetectionCoordinator([strategy]);
        coordinator.BeginNavigation(new Uri("https://www.bilibili.com/video/BV1ab411c7mD"));
        await coordinator.DetectAsync();
        Assert.Equal(MediaDetectionState.Ready, coordinator.Snapshot.State);
        if (viaObservation)
            await coordinator.ObserveAsync(new PlayUrlObservation(new Uri("https://api.bilibili.com/x/player/playurl"), "{}", coordinator.Snapshot.Page!.NavigationGeneration, DateTimeOffset.UtcNow));
        else
            await coordinator.DetectAsync();
        Assert.Equal(MediaDetectionState.Ready, coordinator.Snapshot.State);
        Assert.NotNull(coordinator.Snapshot.Media);
        Assert.Null(coordinator.Snapshot.ErrorCode);
    }

    private sealed class BlockingStrategy : IMediaDetectionStrategy
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "fixture";
        public void Complete() => _gate.SetResult();
        public async Task<MediaDetectionResult> TryResolveAsync(PageIdentity page, CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return new(true, MediaDetectionState.Ready, new MediaFingerprint(page.PageUrl, page.Bvid, page.Aid, page.Cid, page.EpisodeId, 80, "AVC", page.NavigationGeneration), new object());
        }
    }

    private sealed class ThrowingStrategy : IMediaDetectionStrategy
    {
        public string Name => "throwing";
        public Task<MediaDetectionResult> TryResolveAsync(PageIdentity page, CancellationToken cancellationToken) => throw new InvalidOperationException("fixture failure");
    }
}
