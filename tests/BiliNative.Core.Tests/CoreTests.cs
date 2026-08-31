using BiliNative.Core;
using BiliNative.Infrastructure;
using Xunit;

namespace BiliNative.Core.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("a<b>:c?.mp4", "a_b__c_.mp4")]
    [InlineData("CON", "_CON")]
    [InlineData("  title. ", "title")]
    public void SanitizerRemovesWindowsUnsafeNames(string value, string expected)
    {
        Assert.Equal(expected, new FileNameSanitizer().Sanitize(value));
    }

    [Fact]
    public void StateMachineRejectsIllegalTransition()
    {
        Assert.False(DownloadTaskStateMachine.CanTransition(DownloadTaskState.Completed, DownloadTaskState.Queued));
        Assert.Throws<InvalidOperationException>(() => DownloadTaskStateMachine.Transition(DownloadTaskState.Completed, DownloadTaskState.Queued));
    }

    [Theory]
    [InlineData(DownloadTaskState.DownloadingVideo, DownloadTaskState.Finalizing)]
    [InlineData(DownloadTaskState.DownloadingAudio, DownloadTaskState.Finalizing)]
    [InlineData(DownloadTaskState.DownloadingSegments, DownloadTaskState.Finalizing)]
    [InlineData(DownloadTaskState.Failed, DownloadTaskState.Resolving)]
    public void StateMachineAllowsRealDownloadCompletionAndRetryPaths(DownloadTaskState from, DownloadTaskState to)
    {
        Assert.True(DownloadTaskStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData("v1.0.0-beta.1", "1.0.0-beta.2", true)]
    [InlineData("1.0.0-beta.2", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0-beta.9", false)]
    public void SemanticVersionsComparePrereleases(string current, string candidate, bool candidateIsNewer)
    {
        Assert.True(SemanticVersion.TryParse(current, out var left));
        Assert.True(SemanticVersion.TryParse(candidate, out var right));
        Assert.Equal(candidateIsNewer, right.CompareTo(left) > 0);
    }
}
