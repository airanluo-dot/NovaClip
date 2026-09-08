using System.Security.Cryptography;
using System.Text.Json;
using NovaClip.Updater;
using Xunit;

namespace NovaClip.Updater.Tests;

public sealed class PortableUpdateTransactionTests
{
    [Fact]
    public void RollbackRestoresOldFilesAndPreservesUserData()
    {
        var root = CreateRoot();
        try
        {
            var source = Path.Combine(root, "source");
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);

            WritePackage(target, "old", includeStaleFile: true);
            File.WriteAllText(Path.Combine(target, "settings.json"), "user-settings");
            WritePackage(source, "new", includeStaleFile: false);

            var transaction = PortableUpdateTransaction.Begin(source, target);

            Assert.Equal("new-NovaClip.exe", File.ReadAllText(Path.Combine(target, "NovaClip.exe")));
            Assert.False(File.Exists(Path.Combine(target, "old.dll")));
            Assert.Equal("user-settings", File.ReadAllText(Path.Combine(target, "settings.json")));

            transaction.Rollback();

            Assert.Equal("old-NovaClip.exe", File.ReadAllText(Path.Combine(target, "NovaClip.exe")));
            Assert.True(File.Exists(Path.Combine(target, "old.dll")));
            Assert.Equal("user-settings", File.ReadAllText(Path.Combine(target, "settings.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void CommitRemovesFilesOwnedOnlyByThePreviousManifest()
    {
        var root = CreateRoot();
        try
        {
            var source = Path.Combine(root, "source");
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);

            WritePackage(target, "old", includeStaleFile: true);
            File.WriteAllText(Path.Combine(target, "Downloads.txt"), "user-data");
            WritePackage(source, "new", includeStaleFile: false);

            var transaction = PortableUpdateTransaction.Begin(source, target);
            transaction.Commit();

            Assert.Equal("new-NovaClip.exe", File.ReadAllText(Path.Combine(target, "NovaClip.exe")));
            Assert.False(File.Exists(Path.Combine(target, "old.dll")));
            Assert.Equal("user-data", File.ReadAllText(Path.Combine(target, "Downloads.txt")));
            Assert.True(File.Exists(Path.Combine(target, "novaclip-package-manifest.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static void WritePackage(string directory, string prefix, bool includeStaleFile)
    {
        var files = new List<(string Name, string Content)>
        {
            ("NovaClip.exe", prefix + "-NovaClip.exe"),
            ("NovaClip.Updater.exe", prefix + "-NovaClip.Updater.exe"),
            ("resources.pri", prefix + "-resources.pri")
        };
        if (includeStaleFile) files.Add(("old.dll", "old-only"));

        var entries = new List<object>();
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file.Name);
            File.WriteAllText(path, file.Content);
            entries.Add(new
            {
                path = file.Name,
                size = new FileInfo(path).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            });
        }

        var manifest = new
        {
            schemaVersion = 1,
            product = "NovaClip",
            version = "1.0.0-beta.7",
            files = entries
        };
        File.WriteAllText(
            Path.Combine(directory, "novaclip-package-manifest.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "NovaClipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRoot(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
