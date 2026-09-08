using System.Security.Cryptography;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Updater;

internal sealed class PortableUpdateTransaction
{
    private const string ManifestName = "novaclip-package-manifest.json";
    private const int MaxManifestBytes = 2_000_000;
    private const int MaxManifestEntries = 100_000;
    private const long MaxFileBytes = 4_000_000_000;
    private readonly string _source;
    private readonly string _target;
    private readonly string _stateRoot;
    private readonly string _backupRoot;
    private readonly string _journalPath;
    private readonly JournalDocument _journal;
    private readonly PackageManifest _manifest;

    private PortableUpdateTransaction(
        string source,
        string target,
        string stateRoot,
        string backupRoot,
        string journalPath,
        JournalDocument journal,
        PackageManifest manifest)
    {
        _source = source;
        _target = target;
        _stateRoot = stateRoot;
        _backupRoot = backupRoot;
        _journalPath = journalPath;
        _journal = journal;
        _manifest = manifest;
    }

    public static PortableUpdateTransaction Begin(string source, string target)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        RecoverPending(target);

        var manifestPath = Path.Combine(source, ManifestName);
        var manifest = PackageManifest.Load(manifestPath);
        manifest.Validate();
        ValidateSource(source, manifest);

        var stateRoot = Path.Combine(Path.GetDirectoryName(target)!, ".novaclip-update");
        Directory.CreateDirectory(stateRoot);
        var backupRoot = Path.Combine(stateRoot, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        var journalPath = Path.Combine(stateRoot, "transaction-" + Guid.NewGuid().ToString("N") + ".json");

        var oldManifest = PackageManifest.TryLoad(Path.Combine(target, ManifestName));
        var oldPaths = oldManifest?.Files.Select(file => file.Path).Where(IsSafeRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var newPaths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var touched = newPaths
            .Concat(oldPaths)
            .Append(ManifestName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsSafeRelativePath)
            .ToArray();
        var existing = touched.Where(path => File.Exists(ResolveSafe(target, path))).ToArray();

        var journal = new JournalDocument
        {
            Id = Path.GetFileNameWithoutExtension(journalPath),
            Target = target,
            BackupRoot = backupRoot,
            State = "Preparing",
            Touched = touched,
            Existing = existing
        };
        var transaction = new PortableUpdateTransaction(source, target, stateRoot, backupRoot, journalPath, journal, manifest);
        WriteJournal(journalPath, journal);
        try
        {
            transaction.BackupExisting();
            transaction.UpdateState("Applying");
            transaction.ApplyFiles();
            transaction.UpdateState("Committed");
            return transaction;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void Commit()
    {
        try
        {
            if (Directory.Exists(_backupRoot)) Directory.Delete(_backupRoot, recursive: true);
            if (File.Exists(_journalPath)) File.Delete(_journalPath);
            if (Directory.Exists(_stateRoot) && !Directory.EnumerateFileSystemEntries(_stateRoot).Any()) Directory.Delete(_stateRoot);
        }
        catch
        {
            // A committed journal is recoverable and cleanup will be retried on the next updater run.
        }
    }

    public void Rollback()
    {
        try
        {
            foreach (var relative in _journal.Touched)
            {
                if (!IsSafeRelativePath(relative) || IsUserDataPath(relative)) continue;
                var path = ResolveSafe(_target, relative);
                if (File.Exists(path)) File.Delete(path);
            }

            foreach (var relative in _journal.Existing)
            {
                if (!IsSafeRelativePath(relative)) continue;
                var backup = ResolveSafe(_backupRoot, relative);
                var destination = ResolveSafe(_target, relative);
                if (!File.Exists(backup)) continue;
                EnsureDirectoryFor(_target, relative);
                ReplaceFile(backup, destination);
            }
            UpdateState("RolledBack");
        }
        catch
        {
            // Leave the journal for a later recovery attempt.
        }
    }

    private void BackupExisting()
    {
        foreach (var relative in _journal.Existing)
        {
            var source = ResolveSafe(_target, relative);
            var backup = ResolveSafe(_backupRoot, relative);
            EnsureDirectoryFor(_backupRoot, relative);
            File.Copy(source, backup, overwrite: false);
        }
    }

    private void ApplyFiles()
    {
        foreach (var entry in _manifest.Files)
        {
            var source = ResolveSafe(_source, entry.Path);
            var destination = ResolveSafe(_target, entry.Path);
            EnsureDirectoryFor(_target, entry.Path);
            CopyVerified(source, destination, entry);
        }

        var sourceManifest = ResolveSafe(_source, ManifestName);
        var targetManifest = ResolveSafe(_target, ManifestName);
        EnsureDirectoryFor(_target, ManifestName);
        CopyAtomically(sourceManifest, targetManifest);
        foreach (var relative in _journal.Touched.Where(path => !_manifest.Files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)) && !string.Equals(path, ManifestName, StringComparison.OrdinalIgnoreCase)))
        {
            if (IsUserDataPath(relative)) continue;
            var stale = ResolveSafe(_target, relative);
            if (File.Exists(stale)) File.Delete(stale);
        }
    }

    private void UpdateState(string state)
    {
        _journal.State = state;
        WriteJournal(_journalPath, _journal);
    }

    private static void RecoverPending(string target)
    {
        var stateRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, ".novaclip-update");
        if (!Directory.Exists(stateRoot)) return;
        foreach (var journalPath in Directory.EnumerateFiles(stateRoot, "transaction-*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<JournalDocument>(File.ReadAllText(journalPath), JsonOptions);
                if (journal is null || !string.Equals(Path.GetFullPath(journal.Target), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
                if (journal.State == "Committed")
                {
                    if (Directory.Exists(journal.BackupRoot)) Directory.Delete(journal.BackupRoot, true);
                    File.Delete(journalPath);
                    continue;
                }

                var recovery = new PortableUpdateTransaction(
                    target,
                    target,
                    stateRoot,
                    journal.BackupRoot,
                    journalPath,
                    journal,
                    new PackageManifest());
                recovery.Rollback();
            }
            catch
            {
                // A malformed journal is not allowed to mutate arbitrary paths.
            }
        }
    }

    private static void ValidateSource(string source, PackageManifest manifest)
    {
        var total = 0L;
        foreach (var entry in manifest.Files)
        {
            var path = ResolveSafe(source, entry.Path);
            if (!File.Exists(path) || HasReparsePoint(path)) throw new InvalidDataException("The update package contains a missing or reparse-point file.");
            var length = new FileInfo(path).Length;
            if (length != entry.Size) throw new InvalidDataException("The update package file size does not match its manifest.");
            if (entry.Size > MaxFileBytes || total > MaxFileBytes - entry.Size) throw new InvalidDataException("The update package exceeds its file-size budget.");
            total += entry.Size;
            if (!string.Equals(ComputeSha256(path), entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update package digest does not match its manifest.");
        }

        foreach (var required in new[] { "NovaClip.exe", "NovaClip.Updater.exe", "resources.pri" })
        {
            if (!manifest.Files.Any(file => string.Equals(file.Path, required, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The update package is missing a required NovaClip file.");
        }
    }

    private static void CopyVerified(string source, string destination, PackageFile entry)
    {
        CopyAtomically(source, destination);
        var info = new FileInfo(destination);
        if (info.Length != entry.Size || !string.Equals(ComputeSha256(destination), entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The copied update file failed verification.");
        }
    }

    private static void CopyAtomically(string source, string destination)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".update";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            ReplaceFile(temporary, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void ReplaceFile(string source, string destination)
    {
        if (File.Exists(destination))
        {
            try
            {
                File.Replace(source, destination, null, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }
        }
        File.Move(source, destination, overwrite: true);
    }

    private static void EnsureDirectoryFor(string root, string relative)
    {
        var directory = Path.GetDirectoryName(ResolveSafe(root, relative));
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("The update path has no directory.");
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = rootFull;
        var relativeDirectory = Path.GetRelativePath(rootFull, directory);
        if (relativeDirectory is "." or "") return;
        foreach (var segment in relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                if (HasReparsePoint(current)) throw new InvalidDataException("The update path traverses a reparse point.");
            }
            else
            {
                Directory.CreateDirectory(current);
            }
        }
    }

    private static string ResolveSafe(string root, string relative)
    {
        if (!IsSafeRelativePath(relative)) throw new InvalidDataException("The update package contains an unsafe relative path.");
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalized = relative.Replace('\\', '/');
        var candidate = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update package path escaped its root.");
        return candidate;
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000 || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':')) return false;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.None);
        return parts.Length > 0 && parts.All(part => part.Length > 0 && part is not "." and not "..");
    }

    private static bool IsUserDataPath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        var first = normalized.Split('/')[0];
        return first.Equals("WebView2", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("Logs", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("Downloads", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("novaclip.db", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return true; }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteJournal(string path, JournalDocument document)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private sealed class PackageManifest
    {
        public int SchemaVersion { get; set; }
        public string Product { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public List<PackageFile> Files { get; set; } = [];

        public static PackageManifest Load(string path)
        {
            if (!File.Exists(path) || HasReparsePoint(path) || new FileInfo(path).Length > MaxManifestBytes) throw new InvalidDataException("The update package manifest is missing or unsafe.");
            var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("The update package manifest is invalid.");
            manifest.Validate();
            return manifest;
        }

        public static PackageManifest? TryLoad(string path)
        {
            try { return File.Exists(path) && !HasReparsePoint(path) ? Load(path) : null; } catch { return null; }
        }

        public void Validate()
        {
            if (SchemaVersion != 1 || Product != "NovaClip" || !SemanticVersion.TryParse(Version, out _) || Files.Count == 0 || Files.Count > MaxManifestEntries) throw new InvalidDataException("The update package manifest header is invalid.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files)
            {
                if (file is null || !IsSafeRelativePath(file.Path) || !seen.Add(file.Path) || file.Size < 0 || file.Size > MaxFileBytes || file.Sha256.Length != 64 || file.Sha256.Any(character => !char.IsAsciiHexDigit(character))) throw new InvalidDataException("The update package manifest contains an invalid file entry.");
            }
        }
    }

    private sealed class PackageFile
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class JournalDocument
    {
        public string Id { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string BackupRoot { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string[] Touched { get; set; } = [];
        public string[] Existing { get; set; } = [];
    }
}
