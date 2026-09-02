using System.Text.RegularExpressions;
using NovaClip.Contracts;

namespace NovaClip.Bilibili;

public sealed partial class BilibiliUrlResolver : IBilibiliUrlResolver
{
    public bool TryResolve(string input, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && (absolute.Scheme is "http" or "https") && IsBilibiliHost(absolute.Host))
        {
            uri = absolute;
            return true;
        }

        if (value.StartsWith("bilibili.com/", StringComparison.OrdinalIgnoreCase) || value.StartsWith("www.bilibili.com/", StringComparison.OrdinalIgnoreCase) || value.StartsWith("b23.tv/", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate($"https://{value}", UriKind.Absolute, out uri!);
        if (BvidRegex().IsMatch(value))
        {
            uri = new Uri($"https://www.bilibili.com/video/{value}");
            return true;
        }
        if (AvRegex().IsMatch(value))
        {
            uri = new Uri($"https://www.bilibili.com/video/{value.ToLowerInvariant()}");
            return true;
        }
        if (EpisodeRegex().IsMatch(value))
        {
            uri = new Uri($"https://www.bilibili.com/bangumi/play/{value.ToLowerInvariant()}");
            return true;
        }
        return false;
    }

    [GeneratedRegex("^BV[0-9A-Za-z]{10}$", RegexOptions.IgnoreCase)] private static partial Regex BvidRegex();
    [GeneratedRegex("^av[0-9]+$", RegexOptions.IgnoreCase)] private static partial Regex AvRegex();
    [GeneratedRegex("^(ep|ss)[0-9]+$", RegexOptions.IgnoreCase)] private static partial Regex EpisodeRegex();

    private static bool IsBilibiliHost(string host) =>
        host.TrimEnd('.').Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        host.TrimEnd('.').EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        host.TrimEnd('.').Equals("b23.tv", StringComparison.OrdinalIgnoreCase);
}
