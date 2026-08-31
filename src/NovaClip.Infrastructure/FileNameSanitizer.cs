using System.Text;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class FileNameSanitizer : IFileNameSanitizer
{
    private static readonly char[] InvalidCharacters = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).Distinct().ToArray();
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string Sanitize(string value, string fallback = "video")
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var builder = new StringBuilder(candidate.Length);
        foreach (var character in candidate)
        {
            builder.Append(InvalidCharacters.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        candidate = builder.ToString().Trim().TrimEnd('.', ' ');
        if (candidate.Length == 0) candidate = fallback;
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(candidate))) candidate = $"_{candidate}";
        return candidate.Length > 180 ? candidate[..180].TrimEnd('.', ' ') : candidate;
    }

    public string GetAvailablePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        var index = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName} ({index++}){extension}");
        }

        return candidate;
    }
}
