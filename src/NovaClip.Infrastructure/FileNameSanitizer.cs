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
        var builder = new StringBuilder(Math.Min(candidate.Length, 512));
        foreach (var character in candidate.Take(512))
        {
            builder.Append(InvalidCharacters.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        candidate = builder.ToString().Trim().TrimEnd('.', ' ');
        if (candidate.Length == 0) candidate = fallback;
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(candidate))) candidate = $"_{candidate}";
        if (candidate.Length > 180)
        {
            candidate = candidate[..180];
            if (candidate.Length > 0 && char.IsHighSurrogate(candidate[^1])) candidate = candidate[..^1];
            candidate = candidate.TrimEnd('.', ' ');
        }
        return candidate.Length == 0 ? fallback : candidate;
    }

    public string GetAvailablePath(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("The output directory must be absolute.", nameof(directory));
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || fileName.IndexOfAny(['/', '\\', '\0']) >= 0 || Path.GetFileName(fileName) != fileName) throw new ArgumentException("The file name must be a single safe file name.", nameof(fileName));
        Directory.CreateDirectory(directory);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        var index = 1;
        while (File.Exists(candidate))
        {
            if (index > 10_000) throw new IOException("Could not find an available output file name.");
            candidate = Path.Combine(directory, $"{baseName} ({index++}){extension}");
        }

        return candidate;
    }
}
