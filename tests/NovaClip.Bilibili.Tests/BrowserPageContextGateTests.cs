using NovaClip.App;
using NovaClip.Contracts;
using NovaClip.Core;
using Xunit;

namespace NovaClip.Bilibili.Tests;

public sealed class BrowserPageContextGateTests
{
    private const string VideoUrl = "https://www.bilibili.com/video/BV17x411w7KC/";
    private const string HomeUrl = "https://www.bilibili.com/";
    private static readonly Uri Video = new(VideoUrl);
    private static readonly Uri Home = new(HomeUrl);

    [Theory]
    [InlineData(VideoUrl, VideoUrl, HomeUrl, false)]
    [InlineData(HomeUrl, VideoUrl, HomeUrl, false)]
    [InlineData(HomeUrl, HomeUrl, HomeUrl, true)]
    [InlineData(VideoUrl, HomeUrl, HomeUrl, false)]
    [InlineData(VideoUrl + "?p=1", VideoUrl + "?p=1", VideoUrl + "?p=2", false)]
    [InlineData(VideoUrl + "?p=2", VideoUrl + "?p=2", VideoUrl + "?p=2", true)]
    [InlineData(VideoUrl + "?from=search", VideoUrl, VideoUrl + "?tracking=player", true)]
    [InlineData("https://www.bilibili.com/video/BV17X411w7KC/", VideoUrl, VideoUrl, false)]
    public void ContextRequiresBothDomAndIntendedPage(string observed, string source, string intended, bool accepted)
    {
        Assert.Equal(accepted, BrowserPageContextGate.AcceptsContext(new Uri(observed), new Uri(source), new Uri(intended)));
    }

    [Fact]
    public async Task HomeNavigationDoesNotResurrectOutgoingVideoBeforeCommit()
    {
        using var session = new BrowserMediaDetectionSession();
        var initialGeneration = session.BeginNavigation(Video);
        session.UpdatePageContext(VideoContext());
        await ObserveVideoAsync(session, initialGeneration);
        Assert.Equal(MediaDetectionState.Ready, session.Detector.Snapshot.State);

        var homeGeneration = session.BeginNavigation(Home);
        Assert.False(BrowserPageContextGate.AcceptsContext(Video, Video, Home));
        Assert.False(BrowserPageContextGate.AcceptsSource(Video, Home));
        Assert.Null(session.Detector.Snapshot.Media);

        Assert.True(BrowserPageContextGate.AcceptsSource(Home, Home));
        Assert.Equal(homeGeneration, session.UpdatePageContext(new PageIdentity(HomeUrl, null, null, null, null, 0)));
        Assert.True(BrowserPageContextGate.AcceptsContext(Home, Home, Home));
        Assert.False(BrowserPageContextGate.AcceptsContext(Video, Home, Home));
        Assert.Equal(HomeUrl, session.Detector.Snapshot.Page!.PageUrl);
        Assert.Null(session.Detector.Snapshot.Media);
    }

    [Fact]
    public async Task CanceledDestinationCanRestoreTheSurvivingVideoAndRecognizeItAgain()
    {
        using var session = new BrowserMediaDetectionSession();
        session.BeginNavigation(Video);
        var failedGeneration = session.BeginNavigation(Home);
        Assert.True(BrowserPageContextGate.RequiresPageRestore(Video, Home));

        var restoredGeneration = session.UpdatePageContext(new PageIdentity(VideoUrl, null, null, null, null, 0));
        Assert.True(restoredGeneration > failedGeneration);
        Assert.True(BrowserPageContextGate.AcceptsContext(Video, Video, new Uri(session.Detector.Snapshot.Page!.PageUrl)));
        session.UpdatePageContext(VideoContext());
        await ObserveVideoAsync(session, restoredGeneration);

        Assert.Equal(MediaDetectionState.Ready, session.Detector.Snapshot.State);
        Assert.Equal(279786, Assert.IsType<MediaDescriptor>(session.Detector.Snapshot.Media).Cid);
        Assert.False(BrowserPageContextGate.RequiresPageRestore(Video, new Uri(VideoUrl + "?from=search")));
    }

    [Fact]
    public void PendingTargetRejectsAnyMismatchedSourceCommit()
    {
        // This predicate applies to both IsNewDocument values; a late old commit
        // cannot clear the newer native target simply by being a new document.
        Assert.False(BrowserPageContextGate.AcceptsSource(Video, Home));
        Assert.True(BrowserPageContextGate.AcceptsSource(Home, Home));
        Assert.True(BrowserPageContextGate.AcceptsSource(Video, null));
    }

    private static PageIdentity VideoContext() => new(VideoUrl, "BV17x411w7KC", 170001, 279786, null, 0, "Current video");

    private static Task ObserveVideoAsync(BrowserMediaDetectionSession session, long generation) => session.Detector.ObserveAsync(
        new PlayUrlObservation(new Uri("https://api.bilibili.com/x/player/playurl?avid=170001&cid=279786"),
            "{\"code\":0,\"data\":{\"durl\":[{\"url\":\"https://media.example/video.mp4\"}]}}", generation, DateTimeOffset.UtcNow));
}
