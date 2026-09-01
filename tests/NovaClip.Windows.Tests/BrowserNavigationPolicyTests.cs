using NovaClip.Contracts;
using NovaClip.Windows;
using Xunit;

namespace NovaClip.Windows.Tests;

public sealed class BrowserNavigationPolicyTests
{
    private readonly BrowserNavigationPolicy _policy = new();
    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx", BrowserNavigationDecision.NavigateInCurrentView)]
    [InlineData("https://api.bilibili.com/x/player/playurl", BrowserNavigationDecision.NavigateInCurrentView)]
    [InlineData("javascript:alert(1)", BrowserNavigationDecision.Block)]
    [InlineData("file:///c:/secret.txt", BrowserNavigationDecision.Block)]
    public void Evaluates_expected_policy(string value, BrowserNavigationDecision expected) => Assert.Equal(expected, _policy.Evaluate(new Uri(value), BrowserNavigationKind.NewWindow));

    [Fact]
    public void External_new_window_requires_user_choice() => Assert.Equal(BrowserNavigationDecision.AskUser, _policy.Evaluate(new Uri("https://example.com"), BrowserNavigationKind.NewWindow));
}
