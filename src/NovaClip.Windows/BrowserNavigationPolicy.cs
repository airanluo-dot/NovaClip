using NovaClip.Contracts;

namespace NovaClip.Windows;

public sealed class BrowserNavigationPolicy : IBrowserNavigationPolicy
{
    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.OrdinalIgnoreCase) { "file", "javascript", "data", "vbscript" };

    public BrowserNavigationDecision Evaluate(Uri uri, BrowserNavigationKind kind)
    {
        if (uri is null || !uri.IsAbsoluteUri || BlockedSchemes.Contains(uri.Scheme)) return BrowserNavigationDecision.Block;
        if (uri.Scheme is not ("http" or "https")) return BrowserNavigationDecision.Block;
        if (IsBilibiliHost(uri.Host)) return BrowserNavigationDecision.NavigateInCurrentView;
        return kind == BrowserNavigationKind.NewWindow
            ? BrowserNavigationDecision.AskUser
            : BrowserNavigationDecision.OpenInSystemBrowser;
    }

    public static bool IsBilibiliHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var normalized = host.TrimEnd('.');
        return normalized.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("b23.tv", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BrowserHomeService : IBrowserHomeService
{
    public Uri HomeUri { get; } = new("https://www.bilibili.com/");
}
