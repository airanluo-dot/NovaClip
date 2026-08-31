using NovaClip.Contracts;

namespace NovaClip.Bilibili.Tests;

public sealed class NativeRebuildTests
{
    [Theory]
    [InlineData("BV1ab411c7mD", "https://www.bilibili.com/video/BV1ab411c7mD")]
    [InlineData("av170001", "https://www.bilibili.com/video/av170001")]
    [InlineData("ep123", "https://www.bilibili.com/bangumi/play/ep123")]
    public void Resolves_friendly_input(string input, string expected)
    {
        var resolver = new BilibiliUrlResolver();
        Assert.True(resolver.TryResolve(input, out var uri));
        Assert.Equal(expected, uri.ToString().TrimEnd('/'));
    }

    [Fact]
    public async Task Ignores_results_from_old_navigation_generation()
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
}
