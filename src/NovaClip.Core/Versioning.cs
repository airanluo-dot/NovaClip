namespace NovaClip.Core;

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<SemanticVersion>
{
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().TrimStart('v', 'V');
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
        {
            normalized = normalized[..plusIndex];
        }

        var dashIndex = normalized.IndexOf('-');
        var core = dashIndex >= 0 ? normalized[..dashIndex] : normalized;
        var pre = dashIndex >= 0 ? normalized[(dashIndex + 1)..] : null;
        var pieces = core.Split('.');
        if (pieces.Length != 3 || !int.TryParse(pieces[0], out var major) || !int.TryParse(pieces[1], out var minor) || !int.TryParse(pieces[2], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, string.IsNullOrWhiteSpace(pre) ? null : pre);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
        {
            if (i >= leftParts.Length) return -1;
            if (i >= rightParts.Length) return 1;
            var l = leftParts[i];
            var r = rightParts[i];
            var lNumber = int.TryParse(l, out var ln);
            var rNumber = int.TryParse(r, out var rn);
            if (lNumber && rNumber)
            {
                var result = ln.CompareTo(rn);
                if (result != 0) return result;
            }
            else if (lNumber != rNumber)
            {
                return lNumber ? -1 : 1;
            }
            else
            {
                var result = StringComparer.Ordinal.Compare(l, r);
                if (result != 0) return result;
            }
        }
        return 0;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(PreRelease is null ? string.Empty : $"-{PreRelease}")}";
}
