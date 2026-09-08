using System.IO.Compression;

namespace NovaClip.Infrastructure;

public static class PortablePackageExtractor
{
    public const int MaxEntries = 20_000;
    public const long MaxTotalUncompressedBytes = 2_000_000_000;
    public const long MaxSingleFileBytes = 512_000_000;
    public const long MaxCompressionRatio = 1_000;
    private const long DiskSafetyMarginBytes = 128_000_000;

    public static async Task ExtractAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !Path.IsPathRooted(archivePath) || !File.Exists(archivePath))
        {
            throw new FileNotFoundException("The update archive does not exist.", archivePath);
        }
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathRooted(destination))
        {
            throw new ArgumentException("The extraction destination must be absolute.", nameof(destination));
        }

        var destinationRoot = NormalizeRoot(destination);
        Directory.CreateDirectory(destinationRoot);
        EnsureDirectoryIsSafe(destinationRoot);

        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new List<(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeEntryPath(entry.FullName);
            if (relativePath is null) continue;

            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal);
            if (!seen.Add(relativePath)) throw new InvalidDataException("The update archive contains duplicate paths.");
            if (IsSymbolicLink(entry)) throw new InvalidDataException("The update archive contains a symbolic link.");

            if (isDirectory)
            {
                if (entry.Length != 0) throw new InvalidDataException("The update archive contains an invalid directory entry.");
            }
            else
            {
                if (entry.Length < 0 || entry.Length > MaxSingleFileBytes) throw new InvalidDataException("The update archive contains an oversized file.");
                if (total > MaxTotalUncompressedBytes - entry.Length) throw new InvalidDataException("The update archive exceeds its uncompressed-size budget.");
                total += entry.Length;
                if (entry.CompressedLength <= 0 ||
                    entry.CompressedLength > long.MaxValue / MaxCompressionRatio ||
                    entry.Length > entry.CompressedLength * MaxCompressionRatio)
                {
                    throw new InvalidDataException("The update archive exceeds its compression-ratio budget.");
                }
            }

            var fullPath = ResolveSafe(destinationRoot, relativePath);
            entries.Add((entry, relativePath, isDirectory));
            if (fullPath.Length > 32_000) throw new InvalidDataException("The update archive contains an overlong path.");
        }

        if (entries.Count > MaxEntries) throw new InvalidDataException("The update archive contains too many entries.");
        EnsureDiskSpace(destinationRoot, total);

        foreach (var item in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = ResolveSafe(destinationRoot, item.RelativePath);
            if (item.IsDirectory)
            {
                EnsureDirectoryFor(destinationRoot, item.RelativePath);
                continue;
            }

            EnsureDirectoryFor(destinationRoot, item.RelativePath);
            await using var input = item.Entry.Open();
            await using var output = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (copied > item.Entry.Length - read) throw new InvalidDataException("The update archive entry exceeded its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
            }
            if (copied != item.Entry.Length) throw new InvalidDataException("The update archive entry was truncated.");
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (HasReparsePoint(fullPath)) throw new InvalidDataException("The extracted file became a reparse point.");
        }
    }

    public static void ValidateApplicationFiles(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathRooted(destination) || !Directory.Exists(destination))
        {
            throw new InvalidDataException("The extracted update directory does not exist.");
        }

        foreach (var name in new[] { "NovaClip.exe", "NovaClip.Updater.exe", "resources.pri", "novaclip-package-manifest.json" })
        {
            var path = Path.Combine(destination, name);
            if (!File.Exists(path) || HasReparsePoint(path)) throw new InvalidDataException("The extracted update package is incomplete.");
        }
    }

    private static string? NormalizeEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0) return null;
        if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains(':')) throw new InvalidDataException("The update archive contains an absolute path.");
        var parts = normalized.Split('/', StringSplitOptions.None);
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) throw new InvalidDataException("The update archive contains a traversal path.");
        return string.Join("/", parts);
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var attributes = unchecked((uint)entry.ExternalAttributes);
        return ((attributes >> 16) & 0xF000) == 0xA000;
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ResolveSafe(string root, string relative)
    {
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update archive path escaped its destination.");
        return candidate;
    }

    private static void EnsureDirectoryFor(string root, string relative)
    {
        var directory = Path.GetDirectoryName(ResolveSafe(root, relative));
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("The update archive entry has no directory.");
        var rootFull = NormalizeRoot(root);
        var relativeDirectory = Path.GetRelativePath(rootFull, directory);
        if (relativeDirectory is "." or "") return;
        var current = rootFull;
        foreach (var segment in relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                EnsureDirectoryIsSafe(current);
            }
            else
            {
                Directory.CreateDirectory(current);
            }
        }
    }

    private static void EnsureDirectoryIsSafe(string path)
    {
        if (HasReparsePoint(path)) throw new InvalidDataException("The update archive destination contains a reparse point.");
    }

    private static void EnsureDiskSpace(string destination, long totalBytes)
    {
        var root = Path.GetPathRoot(destination);
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available < totalBytes + DiskSafetyMarginBytes) throw new IOException("There is not enough disk space for the update archive.");
        }
        catch (DriveNotFoundException) { }
    }

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }
}
