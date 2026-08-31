using NovaClip.Contracts;

namespace NovaClip.Windows;

public sealed class BrowserNavigationPolicy : IBrowserNavigationPolicy
{
    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.OrdinalIgnoreCase) { "file", "javascript", "data", "vbscript" };

    public BrowserNavigationDecision Evaluate(Uri uri, BrowserNavigationKind kind)
    {
        if (!uri.IsAbsoluteUri || BlockedSchemes.Contains(uri.Scheme)) return BrowserNavigationDecision.Block;
        if (uri.Scheme is not ("http" or "https")) return BrowserNavigationDecision.Block;
        if (IsBilibiliHost(uri.Host)) return BrowserNavigationDecision.NavigateInCurrentView;
        return kind == BrowserNavigationKind.NewWindow
            ? BrowserNavigationDecision.AskUser
            : BrowserNavigationDecision.OpenInSystemBrowser;
    }

    internal static bool IsBilibiliHost(string host) =>
        host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase);
}

public sealed class BrowserHomeService : IBrowserHomeService
{
    public Uri HomeUri { get; } = new("https://www.bilibili.com/");
}
