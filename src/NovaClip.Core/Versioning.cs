using System.Globalization;

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
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
        if (normalized.Length == 0) return false;

        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
        {
            if (!IsIdentifierList(normalized[(plusIndex + 1)..])) return false;
            normalized = normalized[..plusIndex];
        }

        var dashIndex = normalized.IndexOf('-');
        var core = dashIndex >= 0 ? normalized[..dashIndex] : normalized;
        var pre = dashIndex >= 0 ? normalized[(dashIndex + 1)..] : null;
        if (pre is not null && !IsIdentifierList(pre)) return false;

        var pieces = core.Split('.', StringSplitOptions.None);
        if (pieces.Length != 3 || !TryParseCoreNumber(pieces[0], out var major) || !TryParseCoreNumber(pieces[1], out var minor) || !TryParseCoreNumber(pieces[2], out var patch)) return false;
        version = new SemanticVersion(major, minor, patch, pre);
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
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length) return -1;
            if (index >= rightParts.Length) return 1;
            var leftPart = leftParts[index];
            var rightPart = rightParts[index];
            var leftNumeric = IsNumericIdentifier(leftPart);
            var rightNumeric = IsNumericIdentifier(rightPart);
            if (leftNumeric && rightNumeric)
            {
                var leftTrimmed = leftPart.TrimStart('0');
                var rightTrimmed = rightPart.TrimStart('0');
                leftTrimmed = leftTrimmed.Length == 0 ? "0" : leftTrimmed;
                rightTrimmed = rightTrimmed.Length == 0 ? "0" : rightTrimmed;
                var result = leftTrimmed.Length.CompareTo(rightTrimmed.Length);
                if (result != 0) return result;
                result = StringComparer.Ordinal.Compare(leftTrimmed, rightTrimmed);
                if (result != 0) return result;
            }
            else if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }
            else
            {
                var result = StringComparer.Ordinal.Compare(leftPart, rightPart);
                if (result != 0) return result;
            }
        }
        return 0;
    }

    private static bool TryParseCoreNumber(string value, out int number)
    {
        number = 0;
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0') || !value.All(char.IsAsciiDigit)) return false;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsIdentifierList(string value) => value.Length > 0 && value.Split('.').All(IsIdentifier);

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')) return false;
        return !IsNumericIdentifier(value) || value.Length == 1 || value[0] != '0';
    }

    private static bool IsNumericIdentifier(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(PreRelease is null ? string.Empty : $"-{PreRelease}")}";
}
