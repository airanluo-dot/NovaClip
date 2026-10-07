using NovaClip.Contracts;
using NovaClip.Core;
using Xunit;

namespace NovaClip.Bilibili.Tests;

public sealed class MediaDetectionOrderingTests
{
    private const string VideoUrl = "https://www.bilibili.com/video/BV1ab411c7mD";
    private const string DashJson = """
        {"code":0,"data":{"accept_quality":[80,64],"accept_description":["1080P","720P"],"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://media.example/video"},{"id":64,"codecid":7,"base_url":"https://media.example/video-720"}],"audio":[{"id":30280,"base_url":"https://media.example/audio"}]}}}
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NetworkAndPageContextConvergeInEitherOrder(bool networkFirst)
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        if (networkFirst)
        {
            await detector.ObserveAsync(Observation(generation, 101));
            Assert.Equal(MediaDetectionState.WaitingForPageContext, detector.Snapshot.State);
        }
        Assert.Equal(generation, detector.UpdatePageContext(Context(101)));
        if (!networkFirst) await detector.ObserveAsync(Observation(generation, 101));
        var media = Assert.IsType<MediaDescriptor>(detector.Snapshot.Media);
        Assert.Equal(MediaDetectionState.Ready, detector.Snapshot.State);
        Assert.Equal(101, media.Cid);
        Assert.Equal("Current part", media.Title);
        Assert.Equal(2, media.QualityOptions.Count);
        Assert.Equal(2, media.Tracks.Count(track => track.Type == TrackType.Video));
        Assert.NotNull(media.AudioTrack);
    }

    [Fact]
    public async Task ReadyResultSurvivesSameVideoIdentityAndTitleEnrichment()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101) with { Aid = null, Title = null });
        await detector.ObserveAsync(Observation(generation, 101));
        Assert.Equal(generation, detector.UpdatePageContext(Context(101)));
        Assert.Equal(MediaDetectionState.Ready, detector.Snapshot.State);
        Assert.Equal(10, Assert.IsType<MediaDescriptor>(detector.Snapshot.Media).Aid);
        Assert.Equal("Current part", Assert.IsType<MediaDescriptor>(detector.Snapshot.Media).Title);
    }

    [Fact]
    public async Task PartialContextDoesNotForgetKnownContentIdentity()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(Observation(generation, 101));
        Assert.Equal(generation, detector.UpdatePageContext(new PageIdentity(VideoUrl, null, null, null, null, 0)));
        Assert.Equal(101, detector.Snapshot.Page!.Cid);
        Assert.Equal(MediaDetectionState.Ready, detector.Snapshot.State);
    }

    [Fact]
    public async Task PartSwitchRejectsLateOldResultAndSameVideoPrefetch()
    {
        var detector = new MediaDetectionCoordinator([]);
        var oldGeneration = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(Observation(oldGeneration, 101));
        var newGeneration = detector.UpdatePageContext(Context(102) with { PageNumber = 2, PageUrl = VideoUrl + "?p=2" });
        Assert.True(newGeneration > oldGeneration);
        Assert.Null(detector.Snapshot.Media);
        await detector.ObserveAsync(Observation(oldGeneration, 101));
        await detector.ObserveAsync(Observation(newGeneration, 101));
        Assert.Null(detector.Snapshot.Media);
        await detector.ObserveAsync(Observation(newGeneration, 102));
        Assert.Equal(102, Assert.IsType<MediaDescriptor>(detector.Snapshot.Media).Cid);
    }

    [Fact]
    public async Task RapidVideoSwitchRejectsOldBufferedResponseAndRecovers()
    {
        var detector = new MediaDetectionCoordinator([]);
        var oldGeneration = detector.BeginNavigation(new Uri(VideoUrl));
        await detector.ObserveAsync(Observation(oldGeneration, 101));
        var nextUrl = "https://www.bilibili.com/video/BV1xx411c7mD";
        var newGeneration = detector.BeginNavigation(new Uri(nextUrl));
        detector.UpdatePageContext(new PageIdentity(nextUrl, "BV1xx411c7mD", 20, 201, null, 0, "Next"));
        await detector.ObserveAsync(Observation(oldGeneration, 101));
        Assert.Null(detector.Snapshot.Media);
        await detector.ObserveAsync(new PlayUrlObservation(new Uri("https://api.bilibili.com/x/player/playurl?avid=20&cid=201"), DashJson, newGeneration, DateTimeOffset.UtcNow));
        Assert.Equal(201, Assert.IsType<MediaDescriptor>(detector.Snapshot.Media).Cid);
    }

    [Fact]
    public async Task TrackingQueryChangesEnrichButPartQueryChangesAdvanceGeneration()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(Observation(generation, 101));
        Assert.Equal(generation, detector.UpdatePageContext(Context(101) with { PageUrl = VideoUrl + "/?from=search" }));
        Assert.Equal(MediaDetectionState.Ready, detector.Snapshot.State);
        Assert.True(detector.UpdatePageContext(new PageIdentity(VideoUrl + "?p=2", "BV1ab411c7mD", 10, null, null, 0)) > generation);
        Assert.Null(detector.Snapshot.Media);
    }

    [Fact]
    public async Task IncompleteContextIsBoundedAndCanRecoverAfterTerminalPass()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        await detector.ObserveAsync(Observation(generation, 101));
        detector.CompleteObservation(generation);
        Assert.Equal(MediaDetectionState.Unsupported, detector.Snapshot.State);
        Assert.Equal("MEDIA_CONTEXT_INCOMPLETE", detector.Snapshot.ErrorCode);
        detector.UpdatePageContext(Context(101));
        Assert.Equal(MediaDetectionState.Ready, detector.Snapshot.State);
    }

    [Fact]
    public async Task UncorrelatedNetworkResponseCannotBecomeCurrentMedia()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(new PlayUrlObservation(new Uri("https://api.bilibili.com/x/player/playurl"), DashJson, generation, DateTimeOffset.UtcNow));
        Assert.Null(detector.Snapshot.Media);
        detector.CompleteObservation(generation);
        Assert.Equal(MediaDetectionState.Unsupported, detector.Snapshot.State);
    }

    [Fact]
    public async Task PendingPrefetchedOtherPartIsDiscardedWhenCurrentCidArrives()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        await detector.ObserveAsync(Observation(generation, 102));
        await detector.ObserveAsync(Observation(generation, 101));
        detector.UpdatePageContext(Context(101));
        Assert.Equal(101, Assert.IsType<MediaDescriptor>(detector.Snapshot.Media).Cid);
    }

    [Fact]
    public async Task PermissionFailureIsDistinctAndLaterValidResponseRecovers()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(Observation(generation, 101) with { Json = "{\"code\":-101}" });
        Assert.Equal(MediaDetectionState.PermissionDenied, detector.Snapshot.State);
        Assert.Equal("RESOLVE_LOGIN_REQUIRED", detector.Snapshot.ErrorCode);
        await detector.ObserveAsync(Observation(generation, 101));
        Assert.Equal(MediaDetectionState.Ready, detector.Snapshot.State);
        Assert.Null(detector.Snapshot.ErrorCode);
        await detector.ObserveAsync(Observation(generation, 101) with { Json = "not-json" });
        Assert.Equal(MediaDetectionState.Ready, detector.Snapshot.State);
        Assert.NotNull(detector.Snapshot.Media);
    }

    [Fact]
    public async Task PageDataFallbackProvidesDownloadableTracksWithMatchingEvidence()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        const string bare = "{\"dash\":{\"video\":[{\"id\":80,\"base_url\":\"https://media.example/video\"}],\"audio\":[{\"id\":30280,\"base_url\":\"https://media.example/audio\"}]}}";
        await detector.ObservePageDataAsync(bare, Context(101), generation);
        var media = Assert.IsType<MediaDescriptor>(detector.Snapshot.Media);
        Assert.Equal(ResolverStrategy.PageData, media.Source);
        Assert.NotNull(media.VideoTrack);
        Assert.NotNull(media.AudioTrack);
        await detector.ObservePageDataAsync(bare, Context(102), generation);
        Assert.Equal(101, Assert.IsType<MediaDescriptor>(detector.Snapshot.Media).Cid);
    }

    [Fact]
    public async Task BodyIdentityConflictingWithRequestCannotPoisonCurrentState()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(Observation(generation, 101) with { Json = DashJson.Replace("\"data\":{", "\"data\":{\"cid\":102,") });
        Assert.Null(detector.Snapshot.Media);
        Assert.NotEqual(MediaDetectionState.Error, detector.Snapshot.State);
    }

    [Fact]
    public async Task DuplicateObservationKeepsSameDescriptorButRefreshedUrlsReplaceIt()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(Observation(generation, 101));
        var original = detector.Snapshot.Media;
        await detector.ObserveAsync(Observation(generation, 101));
        Assert.Same(original, detector.Snapshot.Media);
        await detector.ObserveAsync(Observation(generation, 101) with { Json = DashJson.Replace("https://media.example/video\"", "https://media.example/video?refreshed=1\"") });
        Assert.NotSame(original, detector.Snapshot.Media);
        Assert.EndsWith("?refreshed=1", Assert.IsType<MediaDescriptor>(detector.Snapshot.Media).VideoTrack!.Urls[0].Url);
    }

    [Fact]
    public async Task UpdatedSecondaryTrackMetadataIsNotSuppressedAsDuplicate()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(Observation(generation, 101));
        var original = detector.Snapshot.Media;
        await detector.ObserveAsync(Observation(generation, 101) with { Json = DashJson.Replace("\"id\":64,", "\"id\":64,\"size\":1234,") });
        Assert.NotSame(original, detector.Snapshot.Media);
        Assert.Equal(1234, Assert.IsType<MediaDescriptor>(detector.Snapshot.Media).Tracks.Single(track => track.QualityId == 64).Size);
    }

    [Fact]
    public async Task DuplicatePageContextPreservesDescriptorForUiCoalescing()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(Observation(generation, 101));
        var original = detector.Snapshot.Media;
        detector.UpdatePageContext(Context(101));
        Assert.Same(original, detector.Snapshot.Media);
    }

    [Fact]
    public async Task PendingObservationQueueRetainsOnlyFourRecentCandidates()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        for (var cid = 100; cid <= 104; cid++) await detector.ObserveAsync(Observation(generation, cid));
        detector.UpdatePageContext(Context(100));
        Assert.Null(detector.Snapshot.Media);
        detector.CompleteObservation(generation);
        Assert.Equal(MediaDetectionState.Unsupported, detector.Snapshot.State);
        await detector.ObserveAsync(Observation(generation, 100));
        Assert.Equal(MediaDetectionState.Ready, detector.Snapshot.State);
    }

    [Fact]
    public void DirectStrategyResultRequiresCurrentResourceAndPartEvidence()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        var foreign = new MediaFingerprint("https://example.com/video/BV1ab411c7mD", "BV1ab411c7mD", 10, 101, null, 80, "AVC", generation);
        Assert.False(detector.TryAcceptResult(generation, new MediaDetectionResult(true, MediaDetectionState.Ready, foreign, new object())));
        var unproven = foreign with { PageUrl = VideoUrl, Cid = null };
        Assert.False(detector.TryAcceptResult(generation, new MediaDetectionResult(true, MediaDetectionState.Ready, unproven, new object())));
        Assert.Null(detector.Snapshot.Media);
    }

    [Fact]
    public void ResetInvalidatesBufferedAndLateEvidence()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.Reset();
        detector.FailObservation(generation, "OLD_FAILURE");
        detector.CompleteObservation(generation);
        Assert.Equal(MediaDetectionState.Idle, detector.Snapshot.State);
        Assert.Null(detector.Snapshot.Page);
        Assert.Null(detector.Snapshot.Media);
    }

    [Fact]
    public async Task DrmMarkerIsUnsupportedAndTerminalSettlementPreservesReason()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        await detector.ObserveAsync(Observation(generation, 101) with { Json = DashJson.Replace("\"data\":{", "\"data\":{\"is_drm\":true,") });
        detector.CompleteObservation(generation);
        Assert.Equal(MediaDetectionState.Unsupported, detector.Snapshot.State);
        Assert.Equal("RESOLVE_DRM_UNSUPPORTED", detector.Snapshot.ErrorCode);
        Assert.Null(detector.Snapshot.Media);
    }

    [Fact]
    public async Task BvidPayloadCaseChangeAdvancesGenerationAndRejectsOldResult()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        detector.UpdatePageContext(Context(101));
        var nextUrl = VideoUrl.Replace("BV1ab", "BV1Ab");
        var nextGeneration = detector.UpdatePageContext(new PageIdentity(nextUrl, "BV1Ab411c7mD", null, null, null, 0));
        Assert.True(nextGeneration > generation);
        await detector.ObserveAsync(Observation(generation, 101));
        Assert.Null(detector.Snapshot.Media);
    }

    [Fact]
    public void BvidPrefixCaseAloneIsCompatibleEvidence()
    {
        var detector = new MediaDetectionCoordinator([]);
        var generation = detector.BeginNavigation(new Uri(VideoUrl));
        var fingerprint = new MediaFingerprint(VideoUrl, "bv1ab411c7mD", null, null, null, 80, "AVC", generation);
        Assert.True(detector.TryAcceptResult(generation, new MediaDetectionResult(true, MediaDetectionState.Ready, fingerprint, new object())));
    }

    private static PageIdentity Context(long cid) => new(VideoUrl, "BV1ab411c7mD", 10, cid, null, 0, "Current part");
    private static PlayUrlObservation Observation(long generation, long cid) => new(
        new Uri($"https://api.bilibili.com/x/player/playurl?avid=10&cid={cid}"), DashJson, generation, DateTimeOffset.UtcNow);
}
