using System.Globalization;
using NovaClip.Contracts;

namespace NovaClip.Bilibili;

/// <summary>Content identity rules shared by observations and page-context enrichment.</summary>
public static class BilibiliMediaIdentity
{
    public static bool IsSamePage(Uri left, Uri right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.IsAbsoluteUri && right.IsAbsoluteUri && SameResource(left.ToString(), right.ToString());
    }

    internal static PageIdentity FromUri(Uri uri, long generation)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var id = segments.LastOrDefault() ?? string.Empty;
        var bvid = id.StartsWith("BV", StringComparison.OrdinalIgnoreCase) ? id : null;
        var aid = id.StartsWith("av", StringComparison.OrdinalIgnoreCase) ? PositiveNumber(id[2..]) : null;
        var episodeId = id.StartsWith("ep", StringComparison.OrdinalIgnoreCase) ? PositiveNumber(id[2..]) : null;
        var query = ReadQuery(uri);
        var pageNumber = PositiveNumber(query.GetValueOrDefault("p"));
        return new PageIdentity(uri.ToString(), bvid, aid, null, episodeId, generation,
            IsBangumi: uri.AbsolutePath.Contains("/bangumi/", StringComparison.OrdinalIgnoreCase),
            PageNumber: pageNumber is > 0 and <= int.MaxValue ? (int)pageNumber.Value : 1);
    }

    internal static PageIdentity FromEndpoint(Uri endpoint, string pageUrl, long generation)
    {
        var query = ReadQuery(endpoint);
        return new PageIdentity(pageUrl, query.GetValueOrDefault("bvid"),
            PositiveNumber(query.GetValueOrDefault("avid")) ?? PositiveNumber(query.GetValueOrDefault("aid")),
            PositiveNumber(query.GetValueOrDefault("cid")),
            PositiveNumber(query.GetValueOrDefault("ep_id")) ?? PositiveNumber(query.GetValueOrDefault("episode_id")),
            generation);
    }

    internal static bool CanEnrich(PageIdentity current, PageIdentity incoming) =>
        SameResource(current.PageUrl, incoming.PageUrl) && !Conflicts(current, incoming) &&
        !(current.PageNumber.HasValue && incoming.PageNumber.HasValue && current.PageNumber != incoming.PageNumber);

    internal static bool Conflicts(PageIdentity left, PageIdentity right) =>
        DifferentBvid(left.Bvid, right.Bvid) || Different(left.Aid, right.Aid) ||
        Different(left.Cid, right.Cid) || Different(left.EpisodeId, right.EpisodeId);

    internal static PageIdentity Merge(PageIdentity current, PageIdentity incoming, long generation) => incoming with
    {
        Bvid = incoming.Bvid ?? current.Bvid,
        Aid = incoming.Aid ?? current.Aid,
        Cid = incoming.Cid ?? current.Cid,
        EpisodeId = incoming.EpisodeId ?? current.EpisodeId,
        Title = incoming.Title ?? current.Title,
        EpisodeTitle = incoming.EpisodeTitle ?? current.EpisodeTitle,
        IsBangumi = incoming.IsBangumi || current.IsBangumi,
        PageNumber = incoming.PageNumber ?? current.PageNumber,
        NavigationGeneration = generation
    };

    internal static bool HasMatchingEvidence(PageIdentity page, PageIdentity evidence)
    {
        // CID is the part identity. An API request for another part must never become
        // a candidate merely because its BV/AV identifier matches the visible video.
        if (page.Cid.HasValue || evidence.Cid.HasValue)
            return page.Cid.HasValue && evidence.Cid.HasValue && page.Cid == evidence.Cid;
        return SameBvid(page.Bvid, evidence.Bvid) || SameValue(page.Aid, evidence.Aid) ||
            SameValue(page.EpisodeId, evidence.EpisodeId);
    }

    private static bool SameResource(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri) || !Uri.TryCreate(right, UriKind.Absolute, out var rightUri)) return false;
        return leftUri.Scheme.Equals(rightUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            leftUri.Host.Equals(rightUri.Host, StringComparison.OrdinalIgnoreCase) &&
            leftUri.AbsolutePath.TrimEnd('/').Equals(rightUri.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal) &&
            FromUri(leftUri, 0).PageNumber == FromUri(rightUri, 0).PageNumber;
    }

    private static Dictionary<string, string> ReadQuery(Uri uri)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            values.TryAdd(Uri.UnescapeDataString(pair[..separator]), Uri.UnescapeDataString(pair[(separator + 1)..]));
        }
        return values;
    }

    private static long? PositiveNumber(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    // The BV prefix is conventional; the remaining Base58 identifier is case sensitive.
    internal static bool SameBvid(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length) return false;
        if (left.Length < 2 || !left.StartsWith("BV", StringComparison.OrdinalIgnoreCase) || !right.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
            return left.Equals(right, StringComparison.Ordinal);
        return left.AsSpan(2).SequenceEqual(right.AsSpan(2));
    }

    private static bool DifferentBvid(string? left, string? right) => left is not null && right is not null && !SameBvid(left, right);
    private static bool Different(long? left, long? right) => left.HasValue && right.HasValue && left != right;
    private static bool SameValue(long? left, long? right) => left.HasValue && right.HasValue && left == right;
}
